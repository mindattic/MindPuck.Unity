// Port of MindPuck/src/core/geometry.ts.
using System;

namespace MindPuck.Core
{
    public struct Vec
    {
        public double X;
        public double Y;

        public Vec(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Polygon helpers. A polygon is a closed loop of vertices (Vec[]), in either winding. It may be concave.
    /// </summary>
    public static class Geometry
    {
        public static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        /// <summary>The point on segment a-b closest to p.</summary>
        public static Vec ClosestOnSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return new Vec(ax + dx * t, ay + dy * t);
        }

        public static bool PointInPolygon(double px, double py, Vec[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vec a = poly[i];
                Vec b = poly[j];
                if ((a.Y > py) != (b.Y > py) && px < ((b.X - a.X) * (py - a.Y)) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>Distance from a point to the outline of a polygon. Zero if the point is inside it.</summary>
        public static double PointPolygonDistance(double px, double py, Vec[] poly)
        {
            if (PointInPolygon(px, py, poly)) return 0;
            double best = double.PositiveInfinity;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vec q = ClosestOnSegment(px, py, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y);
                double d = Hypot(px - q.X, py - q.Y);
                if (d < best) best = d;
            }
            return best;
        }

        private static double Cross(double ox, double oy, double ax, double ay, double bx, double by)
        {
            return (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
        }

        public static bool SegmentsIntersect(Vec a, Vec b, Vec c, Vec d)
        {
            double d1 = Cross(c.X, c.Y, d.X, d.Y, a.X, a.Y);
            double d2 = Cross(c.X, c.Y, d.X, d.Y, b.X, b.Y);
            double d3 = Cross(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            double d4 = Cross(a.X, a.Y, b.X, b.Y, d.X, d.Y);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>Smallest gap between two polygons. Zero if they touch, cross or one contains the other.</summary>
        public static double PolygonDistance(Vec[] p, Vec[] q)
        {
            if (PointInPolygon(p[0].X, p[0].Y, q) || PointInPolygon(q[0].X, q[0].Y, p)) return 0;
            double best = double.PositiveInfinity;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                for (int k = 0, l = q.Length - 1; k < q.Length; l = k++)
                {
                    if (SegmentsIntersect(p[j], p[i], q[l], q[k])) return 0;
                    double d = Math.Min(
                        Dist(p[i], ClosestOnSegment(p[i].X, p[i].Y, q[l].X, q[l].Y, q[k].X, q[k].Y)),
                        Dist(q[k], ClosestOnSegment(q[k].X, q[k].Y, p[j].X, p[j].Y, p[i].X, p[i].Y)));
                    if (d < best) best = d;
                }
            }
            return best;
        }

        public static double Dist(Vec a, Vec b)
        {
            return Hypot(a.X - b.X, a.Y - b.Y);
        }

        private static Vec[] Place(Vec[] points, double cx, double cy, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            var placed = new Vec[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vec p = points[i];
                placed[i] = new Vec(cx + p.X * c - p.Y * s, cy + p.X * s + p.Y * c);
            }
            return placed;
        }

        /// <summary>A rectangle with half-width hw and half-height hh, turned by angle radians about its centre.</summary>
        public static Vec[] RectPolygon(double cx, double cy, double hw, double hh, double angle = 0)
        {
            return Place(
                new[] { new Vec(-hw, -hh), new Vec(hw, -hh), new Vec(hw, hh), new Vec(-hw, hh) },
                cx, cy, angle);
        }

        public static Vec[] RegularPolygon(double cx, double cy, double radius, int sides, double angle = 0)
        {
            var points = new Vec[sides];
            for (int i = 0; i < sides; i++)
            {
                double a = ((double)i / sides) * Math.PI * 2;
                points[i] = new Vec(Math.Cos(a) * radius, Math.Sin(a) * radius);
            }
            return Place(points, cx, cy, angle);
        }

        /// <summary>An L: two arms of the given length and thickness, meeting at a corner near the centre. Concave.</summary>
        public static Vec[] LPolygon(double cx, double cy, double arm, double thick, double angle = 0)
        {
            double o = arm / 2;
            return Place(
                new[]
                {
                    new Vec(-o, -o),
                    new Vec(arm - o, -o),
                    new Vec(arm - o, thick - o),
                    new Vec(thick - o, thick - o),
                    new Vec(thick - o, arm - o),
                    new Vec(-o, arm - o),
                },
                cx, cy, angle);
        }

        /// <summary>An irregular convex-ish rock: radii are the distances of evenly spaced vertices from the centre.</summary>
        public static Vec[] BlobPolygon(double cx, double cy, double[] radii, double angle = 0)
        {
            var points = new Vec[radii.Length];
            for (int i = 0; i < radii.Length; i++)
            {
                double a = ((double)i / radii.Length) * Math.PI * 2;
                points[i] = new Vec(Math.Cos(a) * radii[i], Math.Sin(a) * radii[i]);
            }
            return Place(points, cx, cy, angle);
        }
    }
}
