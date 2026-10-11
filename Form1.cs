using System;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using System.Reflection;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Vorbis;

namespace SteppyBrowser
{
    public partial class Form1 : Form
    {
        private IWavePlayer waveOut;
        private IWaveProvider audioFileReader;
        private XMIPlayer xmiPlayer;
        private SIDPlayer sidPlayer;
        private string rootPath = "";
        private string currentImagePath;
        private string currentModelPath;
        private string currentFilePath = ""; // Track currently playing/displayed file
        private List<string> recentFolders = new List<string>();
        private const string recentFoldersFile = "RecentFolders.xml";
        private System.Windows.Forms.Timer audioScrubTimer;
        private bool audioScrubbing;

        public Form1()
        {
            InitializeComponent();
            StartPosition = FormStartPosition.CenterScreen;
            waveOut = new WaveOutEvent();
            LoadRecentFolders();

            audioScrubTimer = new System.Windows.Forms.Timer();
            audioScrubTimer.Interval = 100;
            audioScrubTimer.Tick += AudioScrubTimer_Tick;
        }

        private void PopulateRecentFoldersMenu(ToolStripMenuItem openRecentMenuItem)
        {
            openRecentMenuItem.DropDownItems.Clear(); // Clear existing items

            if (recentFolders.Count == 0)
            {
                openRecentMenuItem.DropDownItems.Add("(No recent folders)");
                openRecentMenuItem.DropDownItems[0].Enabled = false; // Disable if empty
                return;
            }

            foreach (string folder in recentFolders)
            {
                ToolStripMenuItem recentFolderItem = new ToolStripMenuItem(folder);
                recentFolderItem.Click += RecentFolderItem_Click;
                openRecentMenuItem.DropDownItems.Add(recentFolderItem);
            }
        }

        private void RecentFolderItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem clickedItem = (ToolStripMenuItem)sender;
            rootPath = clickedItem.Text;
            PopulateTreeView(treeView1, rootPath);
        }

        private void OpenFolderMenuItem_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
            {
                if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
                {
                    rootPath = folderBrowserDialog.SelectedPath;
                    AddRecentFolder(rootPath); // Add to recent folders list
                    PopulateTreeView(treeView1, rootPath);
                }
            }
        }
        private void AddRecentFolder(string folderPath)
        {
            if (!recentFolders.Contains(folderPath))
            {
                recentFolders.Insert(0, folderPath); // Add to the beginning of the list

                //Limit the list to a maximum of 10 items
                if (recentFolders.Count > 10)
                {
                    recentFolders.RemoveAt(10);
                }
            }
            else
            {
                recentFolders.Remove(folderPath);
                recentFolders.Insert(0, folderPath);
            }
            SaveRecentFolders(); // Save the updated list
        }

        private void LoadRecentFolders()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(List<string>));
                using (FileStream fileStream = new FileStream(recentFoldersFile, FileMode.Open))
                {
                    recentFolders = (List<string>)serializer.Deserialize(fileStream);
                }
            }
            catch (FileNotFoundException)
            {
                // Handle the case where the file doesn't exist (first run)
                recentFolders = new List<string>();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading recent folders: {ex.Message}");
            }
        }

        private void SaveRecentFolders()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(List<string>));
                using (TextWriter writer = new StreamWriter(recentFoldersFile))
                {
                    serializer.Serialize(writer, recentFolders);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving recent folders: {ex.Message}");
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveRecentFolders();
            waveOut?.Stop();
            waveOut?.Dispose();
            (audioFileReader as WaveStream)?.Dispose();
            (audioFileReader as IDisposable)?.Dispose();
            xmiPlayer?.Stop();
            xmiPlayer?.Dispose();
            StopSidPlayer();
            audioScrubTimer?.Stop();
            audioScrubTimer?.Dispose();
        }

        private void PopulateTreeView(TreeView treeView, string path)
        {
            treeView.BeginUpdate();
            treeView.Nodes.Clear();
            TreeNode rootNode = new TreeNode(path);
            rootNode.Tag = path;
            GetDirectories(rootNode);
            treeView.Nodes.Add(rootNode);
            rootNode.Expand(); // Optionally expand the root
            treeView.EndUpdate();
        }

        private void GetDirectories(TreeNode node)
        {
            try
            {
                string[] subDirectories = Directory.GetDirectories(node.Tag.ToString());
                foreach (string subDirectory in subDirectories)
                {
                    TreeNode subNode = new TreeNode(Path.GetFileName(subDirectory));
                    subNode.Tag = subDirectory;
                    GetDirectories(subNode); // Recursive call for subfolders
                    node.Nodes.Add(subNode);
                }

                // Add files as child nodes
                string[] files = Directory.GetFiles(node.Tag.ToString());
                foreach (string file in files)
                {
                    TreeNode fileNode = new TreeNode(Path.GetFileName(file));
                    fileNode.Tag = file;
                    node.Nodes.Add(fileNode);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Handle access denied errors gracefully (e.g., log, display a message)
                node.Nodes.Add(new TreeNode("<Access Denied>"));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }


        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null || e.Node.Tag == null) return; // Null checks

            string filePath = e.Node.Tag.ToString();
            bool isSameFile = filePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase);

            // If it's the same file, restart playback/refresh display
            if (isSameFile && File.Exists(filePath))
            {
                if (IsAudioFile(filePath))
                {
                    // Restart audio playback
                    waveOut?.Stop();
                    (audioFileReader as WaveStream)?.Dispose();
                    (audioFileReader as IDisposable)?.Dispose();
                    audioFileReader = null;
                    xmiPlayer?.Stop();
                    xmiPlayer?.Dispose();
                    xmiPlayer = null;
                    StopSidPlayer();
                    HideAudioScrubBar();

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".mp3")
                        {
                            audioFileReader = new Mp3FileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".voc")
                        {
                            audioFileReader = VOCPlayer.CreateWaveProvider(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".sid")
                        {
                            PlaySidFile(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                        }

                        if (extension != ".xmi" && extension != ".voc" && extension != ".sid")
                        {
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error playing audio: {ex.Message}";
                    }
                    return; // Early return since we've handled the restart
                }
                else if (IsTextFile(filePath))
                {
                    // Refresh text display
                    try
                    {
                        textBox1.Text = File.ReadAllText(filePath);
                        toolStripStatusLabel1.Text = $"Length={textBox1.Text.Length}";
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error reading textfile: {ex.Message}";
                    }
                    return; // Early return since we've handled the refresh
                }
                else if (IsImageFile(filePath))
                {
                    // Refresh image display
                    DisplayImage(filePath);
                    return; // Early return since we've handled the refresh
                }
                else if (IsModelFile(filePath))
                {
                    DisplayModel(filePath);
                    return;
                }
            }

            // Different file - stop current playback and start new one
            waveOut?.Stop();
            (audioFileReader as WaveStream)?.Dispose();
            (audioFileReader as IDisposable)?.Dispose();
            audioFileReader = null;
            xmiPlayer?.Stop();
            xmiPlayer?.Dispose();
            xmiPlayer = null;
            StopSidPlayer();
            HideAudioScrubBar();
            textBox1.Text = "";
            pictureBox1.Image = null;
            currentImagePath = null;
            currentModelPath = null;
            modelViewerControl.ClearMesh();
            modelViewerControl.Visible = false;
            toolStripStatusLabel1.Text = "";

            if (File.Exists(filePath))
            {
                if (IsAudioFile(filePath))
                {
                    textBox1.Visible = true;
                    pictureBox1.Visible = false;
                    modelViewerControl.Visible = false;

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                            
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                        else if (extension == ".mp3")
                        {
                            audioFileReader = new Mp3FileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                            
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                        else if (extension == ".voc")
                        {
                            audioFileReader = VOCPlayer.CreateWaveProvider(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".sid")
                        {
                            PlaySidFile(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();

                            try
                            {
                                var file = TagLib.File.Create(filePath); // Use TagLib#
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose(); // Important: Dispose of the TagLib# file object
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error playing audio: {ex.Message}";
                    }
                }
                else if (IsTextFile(filePath))
                {
                    textBox1.Visible = true;
                    pictureBox1.Visible = false;
                    modelViewerControl.Visible = false;

                    try
                    {
                        textBox1.Text = File.ReadAllText(filePath);
                        toolStripStatusLabel1.Text = $"Length={textBox1.Text.Length}";
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error reading textfile: {ex.Message}";
                    }
                }
                else if (IsImageFile(filePath))
                {
                    textBox1.Visible = false;
                    pictureBox1.Visible = true;
                    modelViewerControl.Visible = false;
                    modelViewerControl.ClearMesh();
                    currentModelPath = null;
                    currentImagePath = filePath;
                    DisplayImage(filePath);
                }
                else if (IsModelFile(filePath))
                {
                    textBox1.Visible = false;
                    pictureBox1.Visible = false;
                    modelViewerControl.Visible = true;
                    currentImagePath = null;
                    currentModelPath = filePath;
                    DisplayModel(filePath);
                }
            }

            // Update current file path
            currentFilePath = filePath;
        }

        private void treeView1_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // This event fires on every click, even when clicking the same node
            if (e.Node == null || e.Node.Tag == null) return;
            if (e.Button != MouseButtons.Left) return; // Only handle left clicks

            string filePath = e.Node.Tag.ToString();
            
            // Check if this is the same node that's currently selected AND the same file
            if (treeView1.SelectedNode == e.Node && 
                filePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase) && 
                File.Exists(filePath))
            {
                // Same file clicked again - restart playback or refresh display
                if (IsAudioFile(filePath))
                {
                    // Restart audio playback
                    waveOut?.Stop();
                    (audioFileReader as WaveStream)?.Dispose();
                    (audioFileReader as IDisposable)?.Dispose();
                    audioFileReader = null;
                    xmiPlayer?.Stop();
                    xmiPlayer?.Dispose();
                    xmiPlayer = null;
                    StopSidPlayer();
                    HideAudioScrubBar();

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                            
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                        else if (extension == ".mp3")
                        {
                            audioFileReader = new Mp3FileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                            
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                        else if (extension == ".voc")
                        {
                            audioFileReader = VOCPlayer.CreateWaveProvider(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                            ShowAudioScrubBar();
                        }
                        else if (extension == ".sid")
                        {
                            PlaySidFile(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            ShowAudioScrubBar();
                            
                            try
                            {
                                var file = TagLib.File.Create(filePath);
                                toolStripStatusLabel1.Text = $"Duration={file.Properties.Duration}";
                                textBox1.Text = $"Title: {file.Tag.Title}" + Environment.NewLine
                                               + $"Artist: {file.Tag.FirstPerformer}";
                                file.Dispose();
                            }
                            catch (Exception ex)
                            {
                                textBox1.Text = $"Error reading audio tags: {ex.Message}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error playing audio: {ex.Message}";
                    }
                }
                else if (IsTextFile(filePath))
                {
                    // Refresh text display
                    try
                    {
                        textBox1.Text = File.ReadAllText(filePath);
                        toolStripStatusLabel1.Text = $"Length={textBox1.Text.Length}";
                    }
                    catch (Exception ex)
                    {
                        textBox1.Text = $"Error reading textfile: {ex.Message}";
                    }
                }
                else if (IsImageFile(filePath))
                {
                    // Refresh image display
                    DisplayImage(filePath);
                }
                else if (IsModelFile(filePath))
                {
                    DisplayModel(filePath);
                }
            }
        }

        private bool IsAudioFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".wav" || extension == ".mp3" || extension == ".wma" || extension == ".ogg" || extension == ".voc" || extension == ".xmi" || extension == ".sid";
        }

        private bool IsTextFile(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            return ext == ".txt" || ext == ".log" || ext == ".url";
        }

        private void PictureBox1_Resize(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(currentImagePath))
            {
                DisplayImage(currentImagePath); // Refresh the image
            }
            else if (!string.IsNullOrEmpty(currentModelPath))
            {
                modelViewerControl.Invalidate();
            }
        }

        private void DisplayImage(string filePath)
        {
            try
            {
                Image image;
                string ext = Path.GetExtension(filePath).ToLower();
                if (ext == ".tga" || ext == ".targa")
                    image = TgaImage.LoadBitmap(filePath);
                else if (ext == ".koa" || ext == ".kla" || ext == ".koala")
                    image = KoalaImage.LoadBitmap(filePath);
                else
                    image = Image.FromFile(filePath);

                // Calculate the aspect ratio
                float aspectRatio = (float)image.Width / image.Height;

                toolStripStatusLabel1.Text = $"Width={image.Width} Height={image.Height}";

                // Calculate the new dimensions while maintaining aspect ratio
                int newWidth = pictureBox1.Width;
                int newHeight = (int)(newWidth / aspectRatio);

                if (newHeight > pictureBox1.Height)
                {
                    newHeight = pictureBox1.Height;
                    newWidth = (int)(newHeight * aspectRatio);
                }

                // Create a new Bitmap with the calculated dimensions
                Bitmap bmp = new Bitmap(newWidth, newHeight);

                using (Graphics gfx = Graphics.FromImage(bmp))
                {
                    gfx.InterpolationMode = InterpolationMode.NearestNeighbor;
                    gfx.SmoothingMode = SmoothingMode.None; // Optional: Disable smoothing for the Graphics object as well
                    gfx.DrawImage(image, 0, 0, newWidth, newHeight);
                }

                pictureBox1.SizeMode = PictureBoxSizeMode.CenterImage; // Center the image
                pictureBox1.Image = bmp;
                image.Dispose();
            }
            catch (OutOfMemoryException)
            {
                textBox1.Text = "Error: Image is too large or invalid format.";
                pictureBox1.Image = null;
            }
            catch (FileNotFoundException)
            {
                textBox1.Text = "Error: Image file not found.";
                pictureBox1.Image = null;
            }
            catch (Exception ex)
            {
                textBox1.Text = $"Error displaying image: {ex.Message}";
                pictureBox1.Image = null;
            }
        }

        private bool IsImageFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".jpg" || extension == ".jpeg" || extension == ".png" || extension == ".bmp" || extension == ".gif"
                || extension == ".tga" || extension == ".targa"
                || extension == ".koa" || extension == ".kla" || extension == ".koala";
        }

        private bool IsModelFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".fbx" || extension == ".obj" || extension == ".mdl";
        }

        private void DisplayModel(string filePath)
        {
            try
            {
                ModelMeshStats stats = modelViewerControl.LoadModel(filePath);
                textBox1.Visible = false;
                textBox1.Text = "";
                toolStripStatusLabel1.Text = $"{Path.GetFileName(filePath)} — Verts={stats.Vertices:N0} Tris={stats.Triangles:N0} Polys={stats.Polygons:N0}";
            }
            catch (Exception ex)
            {
                currentModelPath = null;
                textBox1.Visible = true;
                textBox1.Text = $"Error loading model: {ex.Message}";
                toolStripStatusLabel1.Text = "Model load failed";
                modelViewerControl.ClearMesh();
                modelViewerControl.Visible = false;
            }
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (sidPlayer != null && sidPlayer.SongCount > 1 &&
                (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown))
            {
                sidPlayer.ChangeSong(e.KeyCode == Keys.PageDown ? 1 : -1);
                textBox1.Text = sidPlayer.Description;
                toolStripStatusLabel1.Text = sidPlayer.StatusText;
                UpdatePlayPauseButton();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (treeView1.SelectedNode != null)
            {
                switch (e.KeyCode)
                {
                    case Keys.Up:
                        if (treeView1.SelectedNode.PrevVisibleNode != null)
                        {
                            treeView1.SelectedNode = treeView1.SelectedNode.PrevVisibleNode;
                        }
                        break;
                    case Keys.Down:
                        if (treeView1.SelectedNode.NextVisibleNode != null)
                        {
                            treeView1.SelectedNode = treeView1.SelectedNode.NextVisibleNode;
                        }
                        break;

                    case Keys.Left:
                        if (treeView1.SelectedNode.IsExpanded)
                        {
                            treeView1.SelectedNode.Collapse();
                        }
                        else if (treeView1.SelectedNode.Parent != null)
                        {
                            treeView1.SelectedNode = treeView1.SelectedNode.Parent;
                        }

                        break;
                    case Keys.Right:
                        if (!treeView1.SelectedNode.IsExpanded && treeView1.SelectedNode.Nodes.Count > 0)
                        {
                            treeView1.SelectedNode.Expand();
                        }
                        break;
                }
            }
        }

        private void treeView1_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node != null && e.Button == MouseButtons.Left) // Check for left double-click
            {
                string path = e.Node.Tag?.ToString();

                if (string.IsNullOrEmpty(path)) return; // Check for null or empty path

                string actualPath = Path.GetDirectoryName(path);
                if (Directory.Exists(actualPath))
                {
                    try
                    {
                        // Open the folder in Windows Explorer
                        Process.Start("explorer.exe", actualPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error opening folder: {ex.Message}");
                    }
                }
            }
        }

        private void openExplorerMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode node = treeView1.SelectedNode;
            if (node == null || node.Tag == null) return;

            string path = node.Tag.ToString();
            if (string.IsNullOrEmpty(path)) return;

            string explorerPath;
            if (File.Exists(path))
            {
                // For files, open Explorer at the file's parent directory
                explorerPath = Path.GetDirectoryName(path);
            }
            else if (Directory.Exists(path))
            {
                // For directories, open Explorer at that folder
                explorerPath = path;
            }
            else
            {
                return; // Path doesn't exist
            }

            try
            {
                Process.Start("explorer.exe", explorerPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening folder: {ex.Message}");
            }
        }

        private void textBoxSearch_TextChanged(object sender, EventArgs e)
        {
            string searchText = textBoxSearch.Text.ToLower();
            treeView1.BeginUpdate();
            treeView1.Nodes.Clear();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                PopulateTreeView(treeView1, rootPath);
            }
            else
            {
                string[] searchTerms = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                PopulateTreeViewWithFilteredFolders(treeView1, rootPath, searchTerms);
                treeView1.ExpandAll(); // Expand all nodes after populating
            }
            treeView1.EndUpdate();
        }

        private void PopulateTreeViewWithFilteredFolders(TreeView treeView, string path, string[] searchTerms)
        {
            TreeNode rootNode = new TreeNode(path);
            rootNode.Tag = path;

            if (GetDirectoriesWithFilteredFiles(rootNode, searchTerms)) // Check if any matches were found in this branch
            {
                treeView.Nodes.Add(rootNode);
            }
        }

        private bool GetDirectoriesWithFilteredFiles(TreeNode node, string[] searchTerms)
        {
            bool hasMatches = false; // Flag to track matches in this branch

            try
            {
                string[] subDirectories = Directory.GetDirectories(node.Tag.ToString());
                foreach (string subDirectory in subDirectories)
                {
                    TreeNode subNode = new TreeNode(Path.GetFileName(subDirectory));
                    subNode.Tag = subDirectory;

                    string directoryName = Path.GetFileName(subDirectory).ToLower();
                    bool directoryNameMatches = searchTerms.Any(term => directoryName.Contains(term));
                    bool hasMatchingFiles = GetDirectoriesWithFilteredFiles(subNode, searchTerms); // Recursive call, check for matches in subfolders
                    
                    if (directoryNameMatches || hasMatchingFiles)
                    {
                        node.Nodes.Add(subNode);
                        hasMatches = true; // A match was found in a subfolder, so this folder also counts
                    }
                }

                string[] files = Directory.GetFiles(node.Tag.ToString());
                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file).ToLower();
                    if (searchTerms.Any(term => fileName.Contains(term)))
                    {
                        TreeNode fileNode = new TreeNode(Path.GetFileName(file));
                        fileNode.Tag = file;
                        node.Nodes.Add(fileNode);
                        hasMatches = true; // A match was found in this folder
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                node.Nodes.Add(new TreeNode("<Access Denied>"));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }

            return hasMatches; // Return true if any matches were found in this branch
        }

        private void openFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
            {
                if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
                {
                    rootPath = folderBrowserDialog.SelectedPath;
                    textBoxSearch.Clear(); // Clear the search box
                    PopulateTreeView(treeView1, rootPath); // Populate with new root
                    AddRecentFolder(rootPath);
                }
            }
        }
        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void AboutMenuItem_Click(object sender, EventArgs e)
        {
            string assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            string assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            string message = $"{assemblyName}\nVersion: {assemblyVersion}";

            MessageBox.Show(message, "About", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }

        private void openRecentMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            PopulateRecentFoldersMenu(openRecentMenuItem);
        }

        private void DisplayVocFileInfo(string filePath)
        {
            var metadata = VOCFileInfo.GetFileInfo(filePath);
            
            if (!metadata.Found)
            {
                toolStripStatusLabel1.Text = "VOC File";
                textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                               + (metadata.ErrorMessage ?? "Could not extract file information");
                return;
            }
            
            // Format duration
            string durationStr = $"{metadata.Duration.Hours:D2}:{metadata.Duration.Minutes:D2}:{metadata.Duration.Seconds:D2}.{metadata.Duration.Milliseconds:D3}";
            
            toolStripStatusLabel1.Text = $"Duration={durationStr}";
            textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                           + $"Duration: {durationStr}" + Environment.NewLine
                           + $"Sample Rate: {metadata.SampleRate} Hz" + Environment.NewLine
                           + $"Channels: {(metadata.Channels == 1 ? "Mono" : "Stereo")}";
        }

        private void DisplayXmiFileInfo(string filePath)
        {
            var metadata = XMIFileInfo.GetFileInfo(filePath);
            
            if (!metadata.Found)
            {
                toolStripStatusLabel1.Text = "XMI File";
                textBox1.Text = $"XMI File: {Path.GetFileName(filePath)}" + Environment.NewLine
                               + (metadata.ErrorMessage ?? "Could not find EVNT chunk");
                return;
            }
            
            // Format duration
            string durationStr = $"{metadata.Duration.Hours:D2}:{metadata.Duration.Minutes:D2}:{metadata.Duration.Seconds:D2}.{metadata.Duration.Milliseconds:D3}";
            
            toolStripStatusLabel1.Text = $"Duration={durationStr}";
            textBox1.Text = $"XMI File: {Path.GetFileName(filePath)}" + Environment.NewLine
                           + $"Duration: {durationStr}" + Environment.NewLine
                           + $"BPM: {metadata.BPM:F1}" + Environment.NewLine
                           + $"Time Signature: {metadata.TimeSignatureNumerator}/{metadata.TimeSignatureDenominator}" + Environment.NewLine
                           + $"Events: {metadata.EventCount}" + Environment.NewLine
                           + "Using Windows MIDI Synthesizer";
        }

        private void StopSidPlayer()
        {
            sidPlayer?.Dispose();
            sidPlayer = null;
        }

        private void PlaySidFile(string filePath)
        {
            StopSidPlayer();
            sidPlayer = new SIDPlayer(filePath);
            textBox1.Text = sidPlayer.Description;
            toolStripStatusLabel1.Text = sidPlayer.StatusText;
            ShowAudioScrubBar();
        }

        private void ShowAudioScrubBar()
        {
            trackBarXmi.Value = 0;
            UpdateAudioTimeLabel(0, GetAudioDuration());
            UpdatePlayPauseButton();
            panelXmiScrub.Visible = true;
            audioScrubTimer.Start();
        }

        private void HideAudioScrubBar()
        {
            audioScrubTimer.Stop();
            audioScrubbing = false;
            panelXmiScrub.Visible = false;
            trackBarXmi.Value = 0;
            labelXmiTime.Text = "0:00 / 0:00";
            buttonAudioPlayPause.Text = "Pause";
        }

        private double GetAudioPosition()
        {
            if (sidPlayer != null)
                return sidPlayer.CurrentPosition;

            if (xmiPlayer != null)
                return xmiPlayer.CurrentPosition;

            var stream = audioFileReader as WaveStream;
            return stream?.CurrentTime.TotalSeconds ?? 0;
        }

        private double GetAudioDuration()
        {
            if (sidPlayer != null)
                return 0;

            if (xmiPlayer != null)
                return xmiPlayer.Duration;

            var stream = audioFileReader as WaveStream;
            return stream?.TotalTime.TotalSeconds ?? 0;
        }

        private bool IsAudioPlaying()
        {
            if (sidPlayer != null)
                return sidPlayer.IsPlaying;

            if (xmiPlayer != null)
                return xmiPlayer.IsPlaying;

            return waveOut != null && waveOut.PlaybackState == PlaybackState.Playing;
        }

        private void SeekAudio(double positionSeconds)
        {
            if (sidPlayer != null)
                return;

            if (xmiPlayer != null)
            {
                xmiPlayer.Seek(positionSeconds);
                return;
            }

            var stream = audioFileReader as WaveStream;
            if (stream == null)
                return;

            double duration = stream.TotalTime.TotalSeconds;
            if (duration <= 0)
                return;

            if (positionSeconds < 0) positionSeconds = 0;
            if (positionSeconds > duration) positionSeconds = duration;
            stream.CurrentTime = TimeSpan.FromSeconds(positionSeconds);
        }

        private void ToggleAudioPlayPause()
        {
            if (sidPlayer != null)
            {
                sidPlayer.TogglePause();
            }
            else if (xmiPlayer != null)
            {
                if (xmiPlayer.IsPlaying)
                    xmiPlayer.Pause();
                else
                    xmiPlayer.Resume();
            }
            else if (waveOut != null && audioFileReader != null)
            {
                if (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    waveOut.Pause();
                }
                else
                {
                    // Restart from beginning if the stream already finished
                    var stream = audioFileReader as WaveStream;
                    if (stream != null && stream.Position >= stream.Length && stream.Length > 0)
                        stream.Position = 0;
                    waveOut.Play();
                }
            }

            UpdatePlayPauseButton();
        }

        private void UpdatePlayPauseButton()
        {
            buttonAudioPlayPause.Text = IsAudioPlaying() ? "Pause" : "Play";
        }

        private void AudioScrubTimer_Tick(object sender, EventArgs e)
        {
            if (audioScrubbing)
                return;

            if (xmiPlayer == null && audioFileReader == null && sidPlayer == null)
                return;

            double position = GetAudioPosition();
            double duration = GetAudioDuration();
            if (duration <= 0)
            {
                if (trackBarXmi.Value != 0)
                    trackBarXmi.Value = 0;
                UpdateAudioTimeLabel(position, 0);
                UpdatePlayPauseButton();
                return;
            }

            int value = (int)Math.Round(position / duration * trackBarXmi.Maximum);
            if (value < 0) value = 0;
            if (value > trackBarXmi.Maximum) value = trackBarXmi.Maximum;
            trackBarXmi.Value = value;
            UpdateAudioTimeLabel(position, duration);
            UpdatePlayPauseButton();
        }

        private void UpdateAudioTimeLabel(double positionSeconds, double durationSeconds)
        {
            labelXmiTime.Text = $"{FormatAudioTime(positionSeconds)} / {FormatAudioTime(durationSeconds)}";
        }

        private static string FormatAudioTime(double totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        private void buttonAudioPlayPause_Click(object sender, EventArgs e)
        {
            ToggleAudioPlayPause();
        }

        private void trackBarXmi_MouseDown(object sender, MouseEventArgs e)
        {
            audioScrubbing = true;
        }

        private void trackBarXmi_MouseUp(object sender, MouseEventArgs e)
        {
            SeekAudioFromTrackBar();
            audioScrubbing = false;
        }

        private void trackBarXmi_KeyUp(object sender, KeyEventArgs e)
        {
            SeekAudioFromTrackBar();
            audioScrubbing = false;
        }

        private void SeekAudioFromTrackBar()
        {
            double duration = GetAudioDuration();
            if (duration <= 0)
                return;

            double position = trackBarXmi.Value / (double)trackBarXmi.Maximum * duration;
            SeekAudio(position);
            UpdateAudioTimeLabel(position, duration);
            UpdatePlayPauseButton();
        }

        private void trackBarXmi_Scroll(object sender, EventArgs e)
        {
            audioScrubbing = true;
            double duration = GetAudioDuration();
            double position = duration > 0
                ? trackBarXmi.Value / (double)trackBarXmi.Maximum * duration
                : 0;
            UpdateAudioTimeLabel(position, duration);
        }

    }
}