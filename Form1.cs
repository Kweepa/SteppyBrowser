using System;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using System.Reflection;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Xml.Serialization;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Vorbis;

namespace SteppyBrowser
{
    public partial class Form1 : Form
    {
        private IWavePlayer waveOut;
        private IWaveProvider audioFileReader;
        private XMIPlayer xmiPlayer;
        private string rootPath = "";
        private string currentImagePath;
        private string currentFilePath = ""; // Track currently playing/displayed file
        private List<string> recentFolders = new List<string>();
        private const string recentFoldersFile = "RecentFolders.xml";

        public Form1()
        {
            InitializeComponent();
            StartPosition = FormStartPosition.CenterScreen;
            waveOut = new WaveOutEvent();
            LoadRecentFolders();
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
                    xmiPlayer?.Stop();
                    xmiPlayer?.Dispose();
                    xmiPlayer = null;

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                        }
                        else if (extension == ".mp3")
                        {
                            audioFileReader = new Mp3FileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                        }
                        else if (extension == ".voc")
                        {
                            audioFileReader = PlayVoc(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                        }

                        if (extension != ".xmi" && extension != ".voc")
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
            }

            // Different file - stop current playback and start new one
            waveOut?.Stop();
            (audioFileReader as WaveStream)?.Dispose();
            (audioFileReader as IDisposable)?.Dispose();
            xmiPlayer?.Stop();
            xmiPlayer?.Dispose();
            xmiPlayer = null;
            textBox1.Text = "";
            pictureBox1.Image = null;
            toolStripStatusLabel1.Text = "";

            if (File.Exists(filePath))
            {
                if (IsAudioFile(filePath))
                {
                    textBox1.Visible = true;
                    pictureBox1.Visible = false;

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            
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
                            audioFileReader = PlayVoc(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();

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
                    currentImagePath = filePath;
                    DisplayImage(filePath);
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
                    xmiPlayer?.Stop();
                    xmiPlayer?.Dispose();
                    xmiPlayer = null;

                    try
                    {
                        string extension = Path.GetExtension(filePath).ToLower();
                        if (extension == ".xmi")
                        {
                            // XMI file playback using Windows' built-in MIDI synthesizer
                            xmiPlayer = new XMIPlayer(filePath, false);
                            xmiPlayer.Start();
                            DisplayXmiFileInfo(filePath);
                        }
                        else if (extension == ".ogg")
                        {
                            audioFileReader = new VorbisWaveReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            
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
                            audioFileReader = PlayVoc(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            DisplayVocFileInfo(filePath);
                        }
                        else
                        {
                            audioFileReader = new AudioFileReader(filePath);
                            waveOut.Init(audioFileReader);
                            waveOut.Play();
                            
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
            }
        }

        private bool IsAudioFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".wav" || extension == ".mp3" || extension == ".wma" || extension == ".ogg" || extension == ".voc" || extension == ".xmi"; // Add other formats as needed
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
        }

        private void DisplayImage(string filePath)
        {
            try
            {
                Image image = Image.FromFile(filePath);

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
            return extension == ".jpg" || extension == ".jpeg" || extension == ".png" || extension == ".bmp" || extension == ".gif"; // Add more if needed
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
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

        private void textBoxSearch_TextChanged(object sender, EventArgs e)
        {
            string searchText = textBoxSearch.Text.ToLower();
            treeView1.BeginUpdate();
            treeView1.Nodes.Clear();

            if (string.IsNullOrEmpty(searchText))
            {
                PopulateTreeView(treeView1, rootPath);
            }
            else
            {
                PopulateTreeViewWithFilteredFolders(treeView1, rootPath, searchText);
                treeView1.ExpandAll(); // Expand all nodes after populating
            }
            treeView1.EndUpdate();
        }

        private void PopulateTreeViewWithFilteredFolders(TreeView treeView, string path, string filter)
        {
            TreeNode rootNode = new TreeNode(path);
            rootNode.Tag = path;

            if (GetDirectoriesWithFilteredFiles(rootNode, filter)) // Check if any matches were found in this branch
            {
                treeView.Nodes.Add(rootNode);
            }
        }

        private bool GetDirectoriesWithFilteredFiles(TreeNode node, string filter)
        {
            bool hasMatches = false; // Flag to track matches in this branch

            try
            {
                string[] subDirectories = Directory.GetDirectories(node.Tag.ToString());
                foreach (string subDirectory in subDirectories)
                {
                    TreeNode subNode = new TreeNode(Path.GetFileName(subDirectory));
                    subNode.Tag = subDirectory;

                    if (GetDirectoriesWithFilteredFiles(subNode, filter)) // Recursive call, check for matches in subfolders
                    {
                        node.Nodes.Add(subNode);
                        hasMatches = true; // A match was found in a subfolder, so this folder also counts
                    }
                }

                string[] files = Directory.GetFiles(node.Tag.ToString());
                foreach (string file in files)
                {
                    if (Path.GetFileName(file).ToLower().Contains(filter))
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

        public class RawSourceFloatWaveProvider : IWaveProvider
        {
            private readonly float[] source;
            private readonly WaveFormat waveFormat;
            private int position;

            public RawSourceFloatWaveProvider(float[] source, WaveFormat waveFormat)
            {
                this.source = source;
                this.waveFormat = waveFormat;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int sourceIndex = position * waveFormat.Channels;
                int sourceSamplesRemaining = source.Length - sourceIndex;
                int destBytesRemaining = buffer.Length - offset;
                int bytesToCopy = Math.Min(Math.Min(destBytesRemaining, sourceSamplesRemaining * 4), count);

                if (bytesToCopy > 0)
                {
                    Buffer.BlockCopy(source, sourceIndex * 4, buffer, offset, bytesToCopy);
                    position += bytesToCopy / (waveFormat.Channels * 4);
                }
                else
                {
                    return 0;
                }

                return bytesToCopy;
            }

            public WaveFormat WaveFormat
            {
                get { return waveFormat; }
            }
        }
        private void DisplayVocFileInfo(string filePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                
                // Check minimum file size
                if (data.Length < 26)
                {
                    toolStripStatusLabel1.Text = "VOC File";
                    textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                                   + "File too small to parse";
                    return;
                }
                
                string header = "";
                // check header
                for (int i = 0; i < 19 && i < data.Length; i++)
                {
                    header += (char)data[i];
                }
                if (header == "Creative Voice File")
                {
                    int index = 0x1a;
                    int totalSamples = 0;
                    int sampleRate = 0;
                    int channels = 1; // default mono
                    
                    while (index + 4 <= data.Length)
                    {
                        int blockType = data[index];
                        
                        // Check if we have enough bytes to read dataSize
                        if (index + 4 > data.Length) break;
                        
                        int dataSize = data[index + 1] + 256 * (data[index + 2] + 256 * data[index + 3]);
                        index += 4;
                        
                        // Check if dataSize is valid
                        if (dataSize < 0 || index + dataSize > data.Length)
                        {
                            break;
                        }
                        
                        if (blockType == 1)
                        {
                            // regular sound data - need at least 2 more bytes
                            if (index + 2 <= data.Length)
                            {
                                int samplingRate = data[index];
                                if (samplingRate != 256) // Avoid division by zero
                                {
                                    int hertz = -1000000 / (samplingRate - 256);
                                    int numSamples = Math.Max(0, dataSize - 2);
                                    totalSamples += numSamples;
                                    if (sampleRate == 0) sampleRate = hertz;
                                }
                            }
                        }
                        else if (blockType == 8)
                        {
                            // digi sound attr extension block - need at least 4 more bytes
                            if (index + 4 <= data.Length)
                            {
                                int voiceMode = data[index + 3];
                                channels = (voiceMode == 1) ? 2 : 1; // stereo = 1, mono = 0
                            }
                        }
                        else if (blockType == 0)
                        {
                            // Terminator block
                            break;
                        }
                        
                        index += dataSize;
                    }
                    
                    if (sampleRate > 0 && totalSamples > 0)
                    {
                        TimeSpan duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);
                        string durationStr = $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}.{duration.Milliseconds:D3}";
                        
                        toolStripStatusLabel1.Text = $"Duration={durationStr}";
                        textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                                       + $"Duration: {durationStr}" + Environment.NewLine
                                       + $"Sample Rate: {sampleRate} Hz" + Environment.NewLine
                                       + $"Channels: {(channels == 1 ? "Mono" : "Stereo")}";
                    }
                    else
                    {
                        toolStripStatusLabel1.Text = "VOC File";
                        textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}";
                    }
                }
                else
                {
                    toolStripStatusLabel1.Text = "VOC File";
                    textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                                   + "Invalid VOC header";
                }
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = "VOC File";
                textBox1.Text = $"VOC File: {Path.GetFileName(filePath)}" + Environment.NewLine
                               + $"Error reading file info: {ex.Message}";
            }
        }

        private void DisplayXmiFileInfo(string filePath)
        {
            try
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fileStream))
                {
                    long evntChunkStart = 0;
                    long evntChunkEnd = 0;
                    const int FORM_AS_INT = 1179603533;
                    
                    // Find EVNT chunk using recursive search (like XMISequencer)
                    FindXmiEventChunk(reader, fileStream.Length, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
                    
                    if (evntChunkEnd == 0)
                    {
                        toolStripStatusLabel1.Text = "XMI File";
                        textBox1.Text = $"XMI File: {Path.GetFileName(filePath)}" + Environment.NewLine
                                       + "Could not find EVNT chunk";
                        return;
                    }
                    
                    // Parse events to extract tempo, time signature, and calculate duration
                    fileStream.Position = evntChunkStart;
                    double currentTime = 0;
                    double fileBPM = 120.0;
                    int timeSigNum = 4;
                    int timeSigDen = 4;
                    int tpqn = 30;
                    double secondsPerTick = (60.0 / fileBPM) / tpqn;
                    int eventCount = 0;
                    double lastEventTime = 0;
                    
                    byte lastStatus = 0;
                    
                    while (fileStream.Position < evntChunkEnd)
                    {
                        // Read delta time
                        int deltaTime = 0;
                        while (fileStream.Position < evntChunkEnd)
                        {
                            byte timeByte = reader.ReadByte();
                            if ((timeByte & 0x80) != 0)
                            {
                                fileStream.Position--;
                                break;
                            }
                            deltaTime += timeByte;
                            if (timeByte != 127) break;
                        }
                        
                        if (fileStream.Position >= evntChunkEnd) break;
                        
                        currentTime += deltaTime * secondsPerTick;
                        lastEventTime = currentTime;
                        
                        byte status = reader.ReadByte();
                        
                        // Handle Running Status
                        if ((status & 0x80) == 0)
                        {
                            fileStream.Position--;
                            if (lastStatus == 0) break;
                            status = lastStatus;
                        }
                        else
                        {
                            lastStatus = status;
                        }
                        
                        byte eventType = (byte)(status & 0xF0);
                        byte channel = (byte)(status & 0x0F);
                        
                        eventCount++;
                        
                        if (status == 0xFF) // Meta event
                        {
                            byte metaType = reader.ReadByte();
                            
                            // Read VLQ length
                            int length = 0;
                            byte b;
                            do
                            {
                                if (fileStream.Position >= evntChunkEnd) break;
                                b = reader.ReadByte();
                                length = (length << 7) | (b & 0x7F);
                            } while ((b & 0x80) != 0);
                            
                            if (metaType == 0x51 && length == 3) // Set Tempo
                            {
                                int a = reader.ReadByte();
                                int b1 = reader.ReadByte();
                                int c = reader.ReadByte();
                                int microsecondsPerQuarterNote = (a << 16) | (b1 << 8) | c;
                                fileBPM = 60_000_000.0 / microsecondsPerQuarterNote;
                                
                                // Recalculate secondsPerTick
                                double timeSigRatio = (double)timeSigNum / timeSigDen;
                                tpqn = (int)Math.Round(8333.0 / (fileBPM * timeSigRatio));
                                double secondsPerQuarterNote = 60.0 / fileBPM;
                                secondsPerTick = secondsPerQuarterNote / tpqn;
                            }
                            else if (metaType == 0x58 && length == 4) // Time Signature
                            {
                                timeSigNum = reader.ReadByte();
                                byte denominatorPower = reader.ReadByte();
                                reader.ReadByte(); // ClocksPerClick
                                reader.ReadByte(); // 32nds per quarter
                                timeSigDen = 1 << denominatorPower;
                                
                                // Recalculate secondsPerTick
                                double timeSigRatio = (double)timeSigNum / timeSigDen;
                                tpqn = (int)Math.Round(8333.0 / (fileBPM * timeSigRatio));
                                double secondsPerQuarterNote = 60.0 / fileBPM;
                                secondsPerTick = secondsPerQuarterNote / tpqn;
                            }
                            else
                            {
                                // Skip meta event data
                                for (int i = 0; i < length && fileStream.Position < evntChunkEnd; i++)
                                {
                                    reader.ReadByte();
                                }
                            }
                        }
                        else if (eventType == 0x90) // Note On
                        {
                            reader.ReadByte(); // key
                            reader.ReadByte(); // velocity
                            
                            // Read duration VLQ
                            int noteDuration = 0;
                            byte b;
                            do
                            {
                                if (fileStream.Position >= evntChunkEnd) break;
                                b = reader.ReadByte();
                                noteDuration = (noteDuration << 7) | (b & 0x7F);
                            } while ((b & 0x80) != 0);
                        }
                        else if (eventType == 0x80) // Note Off
                        {
                            reader.ReadByte(); // key
                            reader.ReadByte(); // velocity
                        }
                        else if (eventType == 0xC0 || eventType == 0xD0) // Program Change or Channel Pressure
                        {
                            reader.ReadByte(); // 1 data byte
                        }
                        else if (eventType == 0xE0 || eventType == 0xB0 || eventType == 0xA0) // Pitch Bend, Controller, Aftertouch
                        {
                            reader.ReadByte(); // 2 data bytes
                            reader.ReadByte();
                        }
                        else if (status == 0xF0) // SysEx
                        {
                            while (fileStream.Position < evntChunkEnd && reader.ReadByte() != 0xF7) { }
                        }
                    }
                    
                    // Format duration
                    TimeSpan duration = TimeSpan.FromSeconds(lastEventTime);
                    string durationStr = $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}.{duration.Milliseconds:D3}";
                    
                    toolStripStatusLabel1.Text = $"Duration={durationStr}";
                    textBox1.Text = $"XMI File: {Path.GetFileName(filePath)}" + Environment.NewLine
                                   + $"Duration: {durationStr}" + Environment.NewLine
                                   + $"BPM: {fileBPM:F1}" + Environment.NewLine
                                   + $"Time Signature: {timeSigNum}/{timeSigDen}" + Environment.NewLine
                                   + $"Events: {eventCount}" + Environment.NewLine
                                   + "Using Windows MIDI Synthesizer";
                }
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = "XMI File";
                textBox1.Text = $"XMI File: {Path.GetFileName(filePath)}" + Environment.NewLine
                               + $"Error reading file info: {ex.Message}";
            }
        }

        private void FindXmiEventChunk(BinaryReader reader, long containerEndPosition, ref long evntChunkStart, ref long evntChunkEnd, int FORM_AS_INT)
        {
            while (reader.BaseStream.Position < containerEndPosition)
            {
                if (reader.BaseStream.Position + 8 > containerEndPosition) break;

                string chunkID = ReadXmiChunkID(reader);
                int chunkSize = ReadXmiInt32BigEndian(reader);

                if (chunkID == "XMID" && chunkSize == FORM_AS_INT)
                {
                    chunkID = "FORM";
                    chunkSize = ReadXmiInt32BigEndian(reader);
                }
                
                long subChunkEnd = reader.BaseStream.Position + chunkSize;

                switch (chunkID)
                {
                case "EVNT":
                    // This is what we want!
                    evntChunkStart = reader.BaseStream.Position;
                    evntChunkEnd = subChunkEnd;
                    return; // Stop parsing, we're ready
                
                case "FORM":
                    ReadXmiChunkID(reader); // Read and discard Form Type
                    FindXmiEventChunk(reader, subChunkEnd, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
                    break;
                case "CAT ":
                    FindXmiCatalogChunk(reader, subChunkEnd, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
                    break;
                
                default:
                    // Not a container, skip it
                    reader.BaseStream.Seek(subChunkEnd, SeekOrigin.Begin);
                    break;
                }
                
                if (evntChunkEnd > 0) return; // Found it in a sub-chunk

                // Handle IFF Padding
                if (chunkSize % 2 != 0 && reader.BaseStream.Position < containerEndPosition)
                {
                    reader.ReadByte();
                }
            }
        }

        private void FindXmiCatalogChunk(BinaryReader reader, long catalogEndPosition, ref long evntChunkStart, ref long evntChunkEnd, int FORM_AS_INT)
        {
            long startPos = reader.BaseStream.Position;
            string sniff = ReadXmiChunkID(reader);
            reader.BaseStream.Seek(startPos, SeekOrigin.Begin); // Rewind

            if (sniff == "FORM" || sniff == "XMID")
            {
                // Instrument Bank (UW file) - dive in
                FindXmiEventChunk(reader, catalogEndPosition, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
            }
            else if (sniff == "MROF")
            {
                // Song Bank (AW file) - dive in
                FindXmiEventChunk(reader, catalogEndPosition, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
            }
            else
            {
                // It's an offset list. This is the V14 logic.
                int numEntries = reader.ReadInt16(); // Little-Endian
                reader.ReadInt16(); // Skip 2 bytes

                for (int i = 0; i < numEntries; i++)
                {
                    if (reader.BaseStream.Position + 4 > catalogEndPosition) break;
                    int offset = reader.ReadInt32(); // Little-Endian
                    if (offset == 0) continue; 

                    long resumePos = reader.BaseStream.Position; 
                    try
                    {
                        reader.BaseStream.Seek(offset, SeekOrigin.Begin); 
                        // A sub-song is a FORM XMID
                        string formID = ReadXmiChunkID(reader);
                        int formSize = ReadXmiInt32BigEndian(reader);
                        long formEndPosition = reader.BaseStream.Position + formSize;
                        string formType = ReadXmiChunkID(reader);
                        
                        if(formID == "FORM" && formType == "XMID")
                        {
                            FindXmiEventChunk(reader, formEndPosition, ref evntChunkStart, ref evntChunkEnd, FORM_AS_INT);
                        }
                    }
                    catch (Exception)
                    {
                        // Skip invalid entries
                    }
                    
                    reader.BaseStream.Seek(resumePos, SeekOrigin.Begin);
                    if (evntChunkEnd > 0) return; // Found it!
                }
            }
        }

        private string ReadXmiChunkID(BinaryReader reader)
        {
            return new string(reader.ReadChars(4));
        }

        private int ReadXmiInt32BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        private IWaveProvider PlayVoc(string fileName)
        {
            IWaveProvider waveProvider = null;

            byte[] data = File.ReadAllBytes(fileName);
            string header = "";
            // check header
            for (int i = 0; i < 19; i++)
            {
                header += (char)data[i];
            }
            if (header == "Creative Voice File")
            {
                int index = 0x1a;
                int timeConstant = 0;
                int packMethod = 0;
                int voiceMode = 0; // mono
                while (index < data.Length)
                {
                    int blockType = data[index];
                    int dataSize = data[index + 1] + 256 * (data[index + 2] + 256 * data[index + 3]);
                    index += 4;
                    if (blockType == 1)
                    {
                        // regular sound data
                        int samplingRate = data[index];
                        packMethod = data[index + 1];
                        int hertz = -1000000 / (samplingRate - 256);
                        int numSamples = dataSize - 2;
                        float[] samples = new float[numSamples];
                        index += 2;
                        for (int i = 0; i < numSamples; i++)
                        {
                            samples[i] = data[i + index] / 256.0f - 0.5f;
                        }

                        // Create NAudio WaveFormat (Important: Use IeeeFloat)
                        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(hertz, 1);

                        // Create NAudio IWaveProvider from float array
                        waveProvider = new RawSourceFloatWaveProvider(samples, waveFormat);
                    }
                    else if (blockType == 8)
                    {
                        // digi sound attr extension block
                        timeConstant = data[index] + 256 * data[index + 1];
                        packMethod = data[index + 2];
                        voiceMode = data[index + 3];
                    }
                    index += dataSize;
                }
            }

            return waveProvider;
        }
    }
}