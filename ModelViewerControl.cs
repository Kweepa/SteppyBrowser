using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Assimp;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace SteppyBrowser
{
    /// <summary>Vertex / triangle / polygon counts for the loaded mesh.</summary>
    public readonly struct ModelMeshStats
    {
        public ModelMeshStats(int vertices, int triangles, int polygons)
        {
            Vertices = vertices;
            Triangles = triangles;
            Polygons = polygons;
        }

        public int Vertices { get; }
        public int Triangles { get; }
        public int Polygons { get; }
    }

    /// <summary>
    /// Simple OpenGL mesh viewer (orbit camera, Assimp import).
    /// </summary>
    public class ModelViewerControl : UserControl
    {
        private readonly GLControl _gl;
        private readonly List<float> _vertices = new List<float>();
        private readonly List<float> _normals = new List<float>();
        private readonly List<uint> _indices = new List<uint>();
        private bool _hasMesh;
        private float _yaw = 0.5f;
        private float _pitch = 0.35f;
        private float _distance = 3f;
        private Vector3 _center;
        private float _radius = 1f;
        private Point _lastMouse;
        private bool _dragging;
        /// <summary>When false, shaded solid + edge overlay. When true, wireframe only.</summary>
        private bool _wireframeOnly;

        public ModelViewerControl()
        {
            SetStyle(ControlStyles.Opaque, true);
            _gl = new GLControl(new OpenTK.Graphics.GraphicsMode(32, 24, 0, 0), 3, 3, OpenTK.Graphics.GraphicsContextFlags.Default);
            _gl.Dock = DockStyle.Fill;
            _gl.TabStop = true;
            _gl.Load += GlLoad;
            _gl.Paint += GlPaint;
            _gl.Resize += GlResize;
            _gl.MouseDown += GlMouseDown;
            _gl.MouseUp += GlMouseUp;
            _gl.MouseMove += GlMouseMove;
            _gl.MouseWheel += GlMouseWheel;
            _gl.KeyDown += GlKeyDown;
            Controls.Add(_gl);
        }

        private void GlKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.W)
            {
                _wireframeOnly = !_wireframeOnly;
                _gl.Invalidate();
                e.Handled = true;
            }
        }

        private void GlLoad(object sender, EventArgs e)
        {
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.ClearColor(0.15f, 0.15f, 0.18f, 1f);
        }

        private void GlResize(object sender, EventArgs e)
        {
            if (_gl.ClientSize.Width <= 0 || _gl.ClientSize.Height <= 0) return;
            GL.Viewport(0, 0, _gl.ClientSize.Width, _gl.ClientSize.Height);
            _gl.Invalidate();
        }

        private void GlPaint(object sender, PaintEventArgs e)
        {
            if (_gl.ClientSize.Width <= 0 || _gl.ClientSize.Height <= 0) return;

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            float aspect = (float)_gl.ClientSize.Width / _gl.ClientSize.Height;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView((float)(Math.PI / 4.0), aspect, 0.01f, 10000f);

            float cx = _distance * (float)(Math.Cos(_pitch) * Math.Cos(_yaw));
            float cy = _distance * (float)Math.Sin(_pitch);
            float cz = _distance * (float)(Math.Cos(_pitch) * Math.Sin(_yaw));
            Vector3 eye = _center + new Vector3(cx, cy, cz);
            Matrix4 look = Matrix4.LookAt(eye, _center, Vector3.UnitY);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref proj);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref look);

            if (_hasMesh && _indices.Count > 0)
            {
                if (_wireframeOnly)
                {
                    GL.Disable(EnableCap.Lighting);
                    GL.Color3(0.78f, 0.78f, 0.82f);
                    GL.LineWidth(1f);
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                    DrawIndexedTriangles();
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                }
                else
                {
                    GL.Enable(EnableCap.Lighting);
                    GL.Enable(EnableCap.Light0);
                    GL.Enable(EnableCap.ColorMaterial);
                    GL.ColorMaterial(MaterialFace.FrontAndBack, ColorMaterialParameter.AmbientAndDiffuse);

                    float[] lightPos = { 2f, 4f, 3f, 0f };
                    GL.Light(LightName.Light0, LightParameter.Position, lightPos);
                    GL.Light(LightName.Light0, LightParameter.Diffuse, new float[] { 1f, 1f, 1f, 1f });
                    GL.Light(LightName.Light0, LightParameter.Ambient, new float[] { 0.35f, 0.35f, 0.38f, 1f });
                    GL.Color3(0.82f, 0.82f, 0.86f);

#pragma warning disable CS0618
                    GL.Begin(BeginMode.Triangles);
                    for (int i = 0; i < _indices.Count; i++)
                    {
                        uint idx = _indices[i];
                        int vi = (int)idx * 3;
                        GL.Normal3(_normals[vi], _normals[vi + 1], _normals[vi + 2]);
                        GL.Vertex3(_vertices[vi], _vertices[vi + 1], _vertices[vi + 2]);
                    }
                    GL.End();
#pragma warning restore CS0618

                    // Edge overlay: second pass draws triangle outlines on top of the solid.
                    GL.Disable(EnableCap.Lighting);
                    GL.Enable(EnableCap.PolygonOffsetLine);
                    GL.PolygonOffset(-1f, -1f);
                    GL.Color3(0.12f, 0.12f, 0.16f);
                    GL.LineWidth(1f);
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                    DrawIndexedTriangles();
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                    GL.Disable(EnableCap.PolygonOffsetLine);
                }
            }

            _gl.SwapBuffers();
        }

        /// <summary>Indexed triangle soup (vertices only; used for wireframe / line overlay).</summary>
        private void DrawIndexedTriangles()
        {
#pragma warning disable CS0618
            GL.Begin(BeginMode.Triangles);
            for (int i = 0; i < _indices.Count; i++)
            {
                uint idx = _indices[i];
                int vi = (int)idx * 3;
                GL.Vertex3(_vertices[vi], _vertices[vi + 1], _vertices[vi + 2]);
            }
            GL.End();
#pragma warning restore CS0618
        }

        private void GlMouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _lastMouse = e.Location;
            }
        }

        private void GlMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                _dragging = false;
        }

        private void GlMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            int dx = e.X - _lastMouse.X;
            int dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            _yaw += dx * 0.01f;
            _pitch += dy * 0.01f;
            const float lim = (float)(Math.PI / 2.0 - 0.05);
            if (_pitch > lim) _pitch = lim;
            if (_pitch < -lim) _pitch = -lim;
            _gl.Invalidate();
        }

        private void GlMouseWheel(object sender, MouseEventArgs e)
        {
            float factor = e.Delta > 0 ? 0.9f : 1.1f;
            _distance = Math.Max(0.01f, _distance * factor);
            _gl.Invalidate();
        }

        /// <summary>Clears the current mesh.</summary>
        public void ClearMesh()
        {
            _vertices.Clear();
            _normals.Clear();
            _indices.Clear();
            _hasMesh = false;
            _gl.Invalidate();
        }

        /// <summary>Loads a model from disk. Throws on failure.</summary>
        public ModelMeshStats LoadModel(string path)
        {
            ClearMesh();

            const PostProcessSteps flags =
                PostProcessSteps.Triangulate |
                PostProcessSteps.GenerateSmoothNormals |
                PostProcessSteps.JoinIdenticalVertices |
                PostProcessSteps.ImproveCacheLocality;

            Scene scene;
            using (var ctx = new AssimpContext())
            {
                scene = ctx.ImportFile(path, flags);
            }

            if (scene == null || scene.RootNode == null)
                throw new InvalidOperationException("Empty scene.");

            var verts = new List<float>();
            var norms = new List<float>();
            var inds = new List<uint>();
            int polygonCount = 0;
            Accumulate(scene.RootNode, Matrix4x4.Identity, scene, verts, norms, inds, ref polygonCount);

            if (verts.Count == 0)
                throw new InvalidOperationException("No geometry found.");

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < verts.Count; i += 3)
            {
                float x = verts[i], y = verts[i + 1], z = verts[i + 2];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }

            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;

            for (int i = 0; i < verts.Count; i += 3)
            {
                verts[i] -= cx;
                verts[i + 1] -= cy;
                verts[i + 2] -= cz;
            }

            float ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            float rad = ext * 0.5f;
            if (rad < 1e-6f) rad = 1f;

            _vertices.AddRange(verts);
            _normals.AddRange(norms);
            _indices.AddRange(inds);
            _center = Vector3.Zero;
            _radius = rad;
            _distance = Math.Max(rad * 2.8f, 0.5f);
            _hasMesh = true;
            _gl.Invalidate();

            int triCount = _indices.Count / 3;
            return new ModelMeshStats(verts.Count / 3, triCount, polygonCount);
        }

        private static void Accumulate(Node node, Matrix4x4 transform, Scene scene,
            List<float> verts, List<float> norms, List<uint> inds, ref int polygonCount)
        {
            Matrix4x4 world = transform * node.Transform;

            if (node.HasMeshes)
            {
                foreach (int meshIndex in node.MeshIndices)
                {
                    Mesh mesh = scene.Meshes[meshIndex];
                    if (mesh.VertexCount == 0) continue;

                    int baseIdx = verts.Count / 3;

                    for (int i = 0; i < mesh.VertexCount; i++)
                    {
                        Vector3D v = world * mesh.Vertices[i];
                        verts.Add(v.X);
                        verts.Add(v.Y);
                        verts.Add(v.Z);

                        Vector3D n;
                        if (mesh.HasNormals && mesh.Normals.Count > i)
                            n = TransformNormal(world, mesh.Normals[i]);
                        else
                            n = new Vector3D(0, 1, 0);
                        norms.Add(n.X);
                        norms.Add(n.Y);
                        norms.Add(n.Z);
                    }

                    for (int f = 0; f < mesh.FaceCount; f++)
                    {
                        Face face = mesh.Faces[f];
                        if (face.IndexCount != 3) continue;
                        polygonCount++;
                        inds.Add((uint)(baseIdx + face.Indices[0]));
                        inds.Add((uint)(baseIdx + face.Indices[1]));
                        inds.Add((uint)(baseIdx + face.Indices[2]));
                    }
                }
            }

            if (node.HasChildren)
            {
                foreach (Node child in node.Children)
                    Accumulate(child, world, scene, verts, norms, inds, ref polygonCount);
            }
        }

        private static Vector3D TransformNormal(Matrix4x4 m, Vector3D n)
        {
            float nx = m.A1 * n.X + m.A2 * n.Y + m.A3 * n.Z;
            float ny = m.B1 * n.X + m.B2 * n.Y + m.B3 * n.Z;
            float nz = m.C1 * n.X + m.C2 * n.Y + m.C3 * n.Z;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12)
            {
                float s = (float)(1.0 / len);
                nx *= s; ny *= s; nz *= s;
            }
            return new Vector3D(nx, ny, nz);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _gl?.Dispose();
            base.Dispose(disposing);
        }
    }
}
