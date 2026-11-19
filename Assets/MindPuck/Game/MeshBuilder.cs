using System.Collections.Generic;
using UnityEngine;

namespace MindPuck.Game
{
    /// <summary>
    /// Meshes made in code from outlines on the table plane. An outline is a closed loop of (x, z)
    /// points in either winding, and it may be concave (the L shape), so caps are cut by ear clipping.
    /// </summary>
    public static class MeshBuilder
    {
        /// <summary>
        /// Ear clipping. Returns triangles as indices into the outline. Works for any simple polygon,
        /// convex or concave, in either winding.
        /// </summary>
        public static List<int> Triangulate(IList<Vector2> outline)
        {
            var triangles = new List<int>();
            int count = outline.Count;
            if (count < 3) return triangles;

            // Walk the outline counter-clockwise, whichever way it was given.
            float area = 0f;
            for (int i = 0, j = count - 1; i < count; j = i++) area += outline[j].x * outline[i].y - outline[i].x * outline[j].y;
            var remaining = new List<int>(count);
            if (area >= 0f)
            {
                for (int i = 0; i < count; i++) remaining.Add(i);
            }
            else
            {
                for (int i = count - 1; i >= 0; i--) remaining.Add(i);
            }

            while (remaining.Count > 3)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int ia = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int ib = remaining[i];
                    int ic = remaining[(i + 1) % remaining.Count];
                    Vector2 a = outline[ia];
                    Vector2 b = outline[ib];
                    Vector2 c = outline[ic];
                    // A corner that turns right is a dent, not an ear.
                    if (Cross(a, b, c) <= 1e-9f) continue;
                    bool empty = true;
                    for (int k = 0; k < remaining.Count && empty; k++)
                    {
                        int ip = remaining[k];
                        if (ip == ia || ip == ib || ip == ic) continue;
                        if (InTriangle(outline[ip], a, b, c)) empty = false;
                    }
                    if (!empty) continue;
                    triangles.Add(ia);
                    triangles.Add(ib);
                    triangles.Add(ic);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // A degenerate outline (repeated or collinear points). Drop a vertex rather than loop forever.
                    remaining.RemoveAt(0);
                }
            }
            if (remaining.Count == 3)
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[1]);
                triangles.Add(remaining[2]);
            }
            return triangles;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            return Cross(a, b, p) >= 0f && Cross(b, c, p) >= 0f && Cross(c, a, p) >= 0f;
        }

        /// <summary>A prism: the outline at height top, with walls down to bottom. No underside.</summary>
        public static Mesh Extrude(IList<Vector2> outline, float bottom, float top)
        {
            var builder = new Builder();
            builder.Cap(outline, top);
            int count = outline.Count;
            // The outward side of an edge depends on the winding.
            float area = 0f;
            for (int i = 0, j = count - 1; i < count; j = i++) area += outline[j].x * outline[i].y - outline[i].x * outline[j].y;
            float sign = area >= 0f ? 1f : -1f;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 a = outline[j];
                Vector2 b = outline[i];
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-12f) continue;
                Vector3 normal = new Vector3(d.y, 0f, -d.x).normalized * sign;
                var aTop = new Vector3(a.x, top, a.y);
                var bTop = new Vector3(b.x, top, b.y);
                var aBottom = new Vector3(a.x, bottom, a.y);
                var bBottom = new Vector3(b.x, bottom, b.y);
                builder.Triangle(aTop, bTop, bBottom, normal);
                builder.Triangle(aTop, bBottom, aBottom, normal);
            }
            return builder.ToMesh();
        }

        /// <summary>The outline as a flat shape facing up, at the given height.</summary>
        public static Mesh Flat(IList<Vector2> outline, float height)
        {
            var builder = new Builder();
            builder.Cap(outline, height);
            return builder.ToMesh();
        }

        public static Vector2[] Circle(float radius, int segments)
        {
            var points = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                points[i] = new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius);
            }
            return points;
        }

        /// <summary>A flat ring facing up, centred on the origin.</summary>
        public static Mesh Ring(float inner, float outer, int segments, float height)
        {
            var builder = new Builder();
            for (int i = 0; i < segments; i++)
            {
                float a0 = (float)i / segments * Mathf.PI * 2f;
                float a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                var in0 = new Vector3(Mathf.Cos(a0) * inner, height, Mathf.Sin(a0) * inner);
                var out0 = new Vector3(Mathf.Cos(a0) * outer, height, Mathf.Sin(a0) * outer);
                var in1 = new Vector3(Mathf.Cos(a1) * inner, height, Mathf.Sin(a1) * inner);
                var out1 = new Vector3(Mathf.Cos(a1) * outer, height, Mathf.Sin(a1) * outer);
                builder.Triangle(in0, out0, out1, Vector3.up);
                builder.Triangle(in0, out1, in1, Vector3.up);
            }
            return builder.ToMesh();
        }

        private sealed class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private readonly List<int> indices = new List<int>();

            /// <summary>
            /// Add a triangle that faces along normal. Unity draws the side whose corners run clockwise,
            /// which is the side Cross(b - a, c - a) points to, so the corners are swapped when needed.
            /// </summary>
            public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
                {
                    Vector3 swap = b;
                    b = c;
                    c = swap;
                }
                int start = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                normals.Add(normal);
                normals.Add(normal);
                normals.Add(normal);
                indices.Add(start);
                indices.Add(start + 1);
                indices.Add(start + 2);
            }

            public void Cap(IList<Vector2> outline, float height)
            {
                List<int> triangles = Triangulate(outline);
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    Vector2 a = outline[triangles[i]];
                    Vector2 b = outline[triangles[i + 1]];
                    Vector2 c = outline[triangles[i + 2]];
                    Triangle(new Vector3(a.x, height, a.y), new Vector3(b.x, height, b.y), new Vector3(c.x, height, c.y), Vector3.up);
                }
            }

            public Mesh ToMesh()
            {
                var mesh = new Mesh();
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetTriangles(indices, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
