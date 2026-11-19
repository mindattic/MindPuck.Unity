// Port of MindPuck/src/core/navgrid.ts.
using System;
using System.Collections.Generic;

namespace MindPuck.Core
{
    /// <summary>
    /// The table as a grid of cells the centre of the puck may occupy: a cell is free when the whole puck
    /// fits there. Answers "is there a route from here to there" and gives the route.
    /// </summary>
    public sealed class NavGrid
    {
        public const double Cell = 0.25;
        private const double Sqrt2 = 1.4142135623730951;

        public readonly int Cols;
        public readonly int Rows;
        private readonly bool[] blocked;

        /// <summary>
        /// clearance is how far the centre of the puck must stay from every wall: its radius plus a margin.
        /// A negative value means the default, the radius plus 0.06.
        /// </summary>
        public NavGrid(Level level, double clearance = -1)
        {
            if (clearance < 0) clearance = level.PuckRadius + 0.06;
            Cols = (int)Math.Floor(level.Width / Cell + 0.5);
            Rows = (int)Math.Floor(level.Height / Cell + 0.5);
            blocked = new bool[Cols * Rows];
            double c2 = clearance * clearance;
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Cols; col++)
                {
                    double x = (col + 0.5) * Cell;
                    double y = (row + 0.5) * Cell;
                    bool hit = x < clearance || y < clearance || x > level.Width - clearance || y > level.Height - clearance;
                    for (int o = 0; !hit && o < level.Obstacles.Length; o++)
                    {
                        Vec[] poly = level.Obstacles[o];
                        if (Geometry.PointInPolygon(x, y, poly)) hit = true;
                        for (int i = 0, j = poly.Length - 1; !hit && i < poly.Length; j = i++)
                        {
                            Vec q = Geometry.ClosestOnSegment(x, y, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y);
                            if ((x - q.X) * (x - q.X) + (y - q.Y) * (y - q.Y) < c2) hit = true;
                        }
                    }
                    if (hit) blocked[row * Cols + col] = true;
                }
            }
        }

        private int Index(double x, double y)
        {
            int col = Math.Min(Cols - 1, Math.Max(0, (int)Math.Floor(x / Cell)));
            int row = Math.Min(Rows - 1, Math.Max(0, (int)Math.Floor(y / Cell)));
            return row * Cols + col;
        }

        private Vec Centre(int i)
        {
            return new Vec(((i % Cols) + 0.5) * Cell, ((i / Cols) + 0.5) * Cell);
        }

        public bool IsFree(double x, double y)
        {
            return x >= 0 && y >= 0 && x < Cols * Cell && y < Rows * Cell && !blocked[Index(x, y)];
        }

        /// <summary>The free cell nearest to a point: its own cell, or the closest one if that is blocked.</summary>
        private int NearestFree(double x, double y)
        {
            int start = Index(x, y);
            if (!blocked[start]) return start;
            var seen = new bool[blocked.Length];
            var queue = new List<int> { start };
            seen[start] = true;
            for (int head = 0; head < queue.Count; head++)
            {
                int i = queue[head];
                if (!blocked[i]) return i;
                int col = i % Cols;
                int row = (i - col) / Cols;
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int c = col + dc;
                        int r = row + dr;
                        if (c < 0 || r < 0 || c >= Cols || r >= Rows) continue;
                        int n = r * Cols + c;
                        if (!seen[n])
                        {
                            seen[n] = true;
                            queue.Add(n);
                        }
                    }
                }
            }
            return -1;
        }

        /// <summary>True when the puck can travel in a straight line between the two points.</summary>
        public bool LineClear(Vec a, Vec b)
        {
            double length = Geometry.Hypot(b.X - a.X, b.Y - a.Y);
            int steps = Math.Max(1, (int)Math.Ceiling(length / (Cell * 0.5)));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                if (!IsFree(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) return false;
            }
            return true;
        }

        /// <summary>
        /// A* over the free cells, eight directions, never cutting a corner. Returns the route as points
        /// from "from" to "to", or null when the goal cannot be reached.
        /// </summary>
        public Vec[] FindPath(Vec from, Vec to)
        {
            int start = NearestFree(from.X, from.Y);
            int goal = NearestFree(to.X, to.Y);
            if (start < 0 || goal < 0) return null;
            int n = blocked.Length;
            int cols = Cols;
            var g = new double[n];
            var came = new int[n];
            for (int i = 0; i < n; i++)
            {
                g[i] = double.PositiveInfinity;
                came[i] = -1;
            }
            var closed = new bool[n];
            int goalCol = goal % cols;
            int goalRow = (goal - goalCol) / cols;

            // A binary heap of [cell, priority] pairs.
            var heapCell = new List<int>();
            var heapKey = new List<double>();

            g[start] = 0;
            Push(heapCell, heapKey, start, Heuristic(start, cols, goalCol, goalRow));
            while (heapCell.Count > 0)
            {
                int current = Pop(heapCell, heapKey);
                if (closed[current]) continue;
                closed[current] = true;
                if (current == goal) break;
                int col = current % cols;
                int row = (current - col) / cols;
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int c = col + dc;
                        int r = row + dr;
                        if (c < 0 || r < 0 || c >= cols || r >= Rows) continue;
                        int next = r * cols + c;
                        if (blocked[next] || closed[next]) continue;
                        if (dr != 0 && dc != 0 && (blocked[row * cols + c] || blocked[r * cols + col])) continue;
                        double cost = g[current] + (dr != 0 && dc != 0 ? Sqrt2 : 1);
                        if (cost < g[next])
                        {
                            g[next] = cost;
                            came[next] = current;
                            Push(heapCell, heapKey, next, cost + Heuristic(next, cols, goalCol, goalRow));
                        }
                    }
                }
            }
            if (!closed[goal]) return null;

            var path = new List<Vec>();
            for (int i = goal; i != -1; i = came[i]) path.Add(Centre(i));
            path.Reverse();
            // The route ends exactly on the target when its own cell is free.
            if (IsFree(to.X, to.Y)) path[path.Count - 1] = new Vec(to.X, to.Y);
            return path.ToArray();
        }

        private static double Heuristic(int i, int cols, int goalCol, int goalRow)
        {
            int dc = Math.Abs((i % cols) - goalCol);
            int dr = Math.Abs((i / cols) - goalRow);
            return Math.Max(dc, dr) + (Sqrt2 - 1) * Math.Min(dc, dr);
        }

        private static void Push(List<int> heapCell, List<double> heapKey, int cell, double key)
        {
            int i = heapCell.Count;
            heapCell.Add(cell);
            heapKey.Add(key);
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heapKey[parent] <= key) break;
                heapCell[i] = heapCell[parent];
                heapKey[i] = heapKey[parent];
                i = parent;
            }
            heapCell[i] = cell;
            heapKey[i] = key;
        }

        private static int Pop(List<int> heapCell, List<double> heapKey)
        {
            int top = heapCell[0];
            int last = heapCell.Count - 1;
            int cell = heapCell[last];
            double key = heapKey[last];
            heapCell.RemoveAt(last);
            heapKey.RemoveAt(last);
            int size = heapCell.Count;
            if (size > 0)
            {
                int i = 0;
                for (;;)
                {
                    int child = 2 * i + 1;
                    if (child >= size) break;
                    if (child + 1 < size && heapKey[child + 1] < heapKey[child]) child++;
                    if (heapKey[child] >= key) break;
                    heapCell[i] = heapCell[child];
                    heapKey[i] = heapKey[child];
                    i = child;
                }
                heapCell[i] = cell;
                heapKey[i] = key;
            }
            return top;
        }
    }
}
