// Port of MindPuck.Web/src/core/physics.ts.
using System;

namespace MindPuck.Core
{
    /// <summary>
    /// What the gaze does to the puck.
    /// Attract (magnetic) pulls the puck to where you look.
    /// Steer drives the puck toward the side of it you look at: look at its right edge and it goes
    /// right. You keep your eyes on the puck the whole time.
    /// Repel pushes it away: look where you do not want it to go.
    /// The order here is the order F1 steps through (GAZE_MODES in TS).
    /// </summary>
    public enum GazeMode
    {
        Attract,
        Steer,
        Repel,
    }

    /// <summary>Every number that shapes how the table feels. MindPuck/docs/DESIGN.md explains each one.</summary>
    public sealed class PhysicsParams
    {
        /// <summary>Peak push from the gaze point, in units per second squared.</summary>
        public double RepelStrength = 40;
        /// <summary>Distance at which the push has fallen to roughly half. Larger means a softer, wider field.</summary>
        public double RepelSoftness = 2;
        /// <summary>Beyond this distance the gaze does not push at all, so looking away lets the puck coast.</summary>
        public double RepelRange = 6;
        /// <summary>Looking at the very centre of the puck pushes nowhere; the push fades in over this fraction of its radius.</summary>
        public double DeadZone = 0.3;
        /// <summary>Magnetic mode: the pull toward the gaze point once the puck is farther than PullSoftness away.</summary>
        public double PullStrength = 14;
        /// <summary>Magnetic mode: inside this distance the pull eases off in proportion, like a spring, so the puck settles on the gaze point.</summary>
        public double PullSoftness = 1.5;
        /// <summary>Magnetic mode: extra drag while the gaze holds the puck, which stops it circling the point it is pulled to.</summary>
        public double PullDrag = 3.5;
        /// <summary>Steer mode: the drive when you look at the edge of the puck or just beyond it.</summary>
        public double SteerStrength = 9;
        /// <summary>Steer mode: the drive builds from nothing at the centre of the puck to full at this many puck radii out.</summary>
        public double SteerFull = 2.2;
        /// <summary>Steer mode: beyond this many puck radii the drive fades, so looking away lets the puck coast.</summary>
        public double SteerRange = 6;
        /// <summary>Steer mode: extra drag while steering, which keeps the speed manageable.</summary>
        public double SteerDrag = 1.2;
        /// <summary>Air drag: the fraction of speed lost per second while gliding.</summary>
        public double Drag = 0.55;
        /// <summary>Drag on the goal pad, which is what lets the puck come to rest there.</summary>
        public double PadDrag = 3.5;
        /// <summary>A small constant deceleration, so a slow puck actually stops.</summary>
        public double Friction = 0.06;
        /// <summary>Fraction of speed kept, along the normal of the wall, after a bounce.</summary>
        public double Restitution = 0.82;
        /// <summary>Fraction of sliding speed lost along the wall in a bounce.</summary>
        public double WallFriction = 0.03;
        public double MaxSpeed = 14;
        /// <summary>Fixed simulation step, in seconds.</summary>
        public double Dt = 1.0 / 120;
        /// <summary>How long the puck must sit on the pad to win.</summary>
        public double Dwell = 0.5;
        /// <summary>The puck counts as resting on the pad below this speed.</summary>
        public double RestSpeed = 1.5;

        /// <summary>The shared defaults (DEFAULT_PARAMS in TS). Treat it as read-only.</summary>
        public static readonly PhysicsParams Default = new PhysicsParams();
    }

    public static class Physics
    {
        /// <summary>
        /// How hard the gaze pushes the puck from distance d: strongest close in, fading with distance,
        /// nothing past the range, and nothing at dead centre (where there is no direction to push in).
        /// </summary>
        public static double RepelAccel(double d, double puckRadius, PhysicsParams p)
        {
            if (d >= p.RepelRange) return 0;
            double reach = 1 - d / p.RepelRange;
            double core = p.RepelStrength / (1 + (d / p.RepelSoftness) * (d / p.RepelSoftness));
            double edge = p.DeadZone * puckRadius;
            double centre = 1;
            if (d < edge)
            {
                double u = d / edge;
                centre = u * u * (3 - 2 * u);
            }
            return core * reach * reach * centre;
        }

        /// <summary>Magnetic mode: how hard the gaze pulls the puck from distance d. Zero when the puck is on the gaze point.</summary>
        public static double PullAccel(double d, PhysicsParams p)
        {
            return p.PullStrength * Math.Min(d / p.PullSoftness, 1);
        }

        /// <summary>
        /// Steer mode: how hard the puck is driven toward a gaze point at distance d from its centre.
        /// Nothing at the centre (so looking straight at the puck lets it rest), full from the edge
        /// outward, fading to nothing well away from the puck.
        /// </summary>
        public static double SteerAccel(double d, double puckRadius, PhysicsParams p)
        {
            double inner = p.DeadZone * puckRadius;
            double full = p.SteerFull * puckRadius;
            double far = p.SteerRange * puckRadius;
            double fade = far * 1.5;
            if (d <= inner || d >= fade) return 0;
            if (d < full)
            {
                double u = (d - inner) / (full - inner);
                return p.SteerStrength * u * u * (3 - 2 * u);
            }
            if (d <= far) return p.SteerStrength;
            return p.SteerStrength * (1 - (d - far) / (fade - far));
        }

        /// <summary>The win radius: how close the centre of the puck must be to the centre of the goal.</summary>
        public static double WinRadius(Level level)
        {
            return level.Goal.R - level.PuckRadius * 0.6;
        }
    }

    /// <summary>
    /// The puck on the table. A fixed-step simulation: gaze repulsion, drag, and circle-against-polygon
    /// collisions with every wall segment, so the puck bounces correctly off faces and corners alike.
    /// </summary>
    public sealed class PuckSim
    {
        /// <summary>What the gaze does. It can be changed at any time, even mid-flight.</summary>
        public GazeMode Mode = GazeMode.Attract;
        public Vec Pos;
        public Vec Vel;
        /// <summary>Seconds simulated since the last reset.</summary>
        public double Time;
        /// <summary>Seconds the puck has been resting on the pad.</summary>
        public double Dwell;
        public bool Won;
        /// <summary>The hardest hit during the last Advance, as speed into the wall. For sound and effects.</summary>
        public double Impact;
        /// <summary>The push applied in the last step, for drawing.</summary>
        public Vec Push;

        public readonly Level Level;
        public readonly PhysicsParams Params;

        private readonly double[] segments;
        private double accumulator;

        public PuckSim(Level level, PhysicsParams parameters = null)
        {
            Level = level;
            Params = parameters ?? PhysicsParams.Default;
            Pos = new Vec(level.Start.X, level.Start.Y);
            int count = 0;
            foreach (Vec[] poly in level.Obstacles) count += poly.Length;
            segments = new double[count * 4];
            int n = 0;
            foreach (Vec[] poly in level.Obstacles)
            {
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                {
                    segments[n++] = poly[j].X;
                    segments[n++] = poly[j].Y;
                    segments[n++] = poly[i].X;
                    segments[n++] = poly[i].Y;
                }
            }
        }

        public void Reset()
        {
            Pos = new Vec(Level.Start.X, Level.Start.Y);
            Vel = new Vec(0, 0);
            Time = 0;
            Dwell = 0;
            Won = false;
            Impact = 0;
            accumulator = 0;
        }

        /// <summary>Advance by real elapsed time, in whole fixed steps. gaze is null when the eyes are not tracked.</summary>
        public int Advance(double seconds, Vec? gaze)
        {
            Impact = 0;
            accumulator += Math.Min(seconds, 0.1);
            int steps = 0;
            while (accumulator >= Params.Dt)
            {
                accumulator -= Params.Dt;
                Step(gaze);
                steps++;
            }
            return steps;
        }

        /// <summary>One fixed step.</summary>
        public void Step(Vec? gaze)
        {
            PhysicsParams p = Params;
            Level lv = Level;
            double r = lv.PuckRadius;
            double dt = p.Dt;

            double ax = 0;
            double ay = 0;
            if (gaze.HasValue)
            {
                double dx = Pos.X - gaze.Value.X;
                double dy = Pos.Y - gaze.Value.Y;
                double d = Geometry.Hypot(dx, dy);
                if (d > 1e-9)
                {
                    // Repel pushes along the line from the gaze point to the puck; attract pulls back along it.
                    // Steer drives the same way attract pulls, but only while the gaze is on or near the puck.
                    double a = Mode == GazeMode.Attract ? -Physics.PullAccel(d, p)
                        : Mode == GazeMode.Steer ? -Physics.SteerAccel(d, r, p)
                        : Physics.RepelAccel(d, r, p);
                    ax = (dx / d) * a;
                    ay = (dy / d) * a;
                }
            }
            Push.X = ax;
            Push.Y = ay;
            Vel.X += ax * dt;
            Vel.Y += ay * dt;

            double gx = Pos.X - lv.Goal.X;
            double gy = Pos.Y - lv.Goal.Y;
            double goalDist = Geometry.Hypot(gx, gy);
            double extra = !gaze.HasValue ? 0 : Mode == GazeMode.Attract ? p.PullDrag : Mode == GazeMode.Steer ? p.SteerDrag : 0;
            double damp = Math.Exp(-((goalDist < lv.Goal.R ? p.PadDrag : p.Drag) + extra) * dt);
            Vel.X *= damp;
            Vel.Y *= damp;
            double speed = Geometry.Hypot(Vel.X, Vel.Y);
            if (speed > 0)
            {
                double slowed = Math.Min(Math.Max(speed - p.Friction * dt, 0), p.MaxSpeed);
                Vel.X *= slowed / speed;
                Vel.Y *= slowed / speed;
                speed = slowed;
            }

            Pos.X += Vel.X * dt;
            Pos.Y += Vel.Y * dt;

            CollideObstacles();
            CollideArena();

            Time += dt;
            bool resting = Geometry.Hypot(Pos.X - lv.Goal.X, Pos.Y - lv.Goal.Y) <= Physics.WinRadius(lv) && speed < p.RestSpeed;
            Dwell = resting ? Dwell + dt : 0;
            if (Dwell >= p.Dwell) Won = true;
        }

        private void Bounce(double nx, double ny)
        {
            double vn = Vel.X * nx + Vel.Y * ny;
            if (vn >= 0) return;
            PhysicsParams p = Params;
            // Split the velocity into the part going into the wall, which bounces, and the part sliding
            // along it, which loses a little to friction.
            double tx = Vel.X - vn * nx;
            double ty = Vel.Y - vn * ny;
            double keep = 1 - p.WallFriction;
            Vel.X = tx * keep - vn * p.Restitution * nx;
            Vel.Y = ty * keep - vn * p.Restitution * ny;
            if (-vn > Impact) Impact = -vn;
        }

        private void CollideArena()
        {
            Level lv = Level;
            double r = lv.PuckRadius;
            if (Pos.X < r)
            {
                Pos.X = r;
                Bounce(1, 0);
            }
            else if (Pos.X > lv.Width - r)
            {
                Pos.X = lv.Width - r;
                Bounce(-1, 0);
            }
            if (Pos.Y < r)
            {
                Pos.Y = r;
                Bounce(0, 1);
            }
            else if (Pos.Y > lv.Height - r)
            {
                Pos.Y = lv.Height - r;
                Bounce(0, -1);
            }
        }

        private void CollideObstacles()
        {
            double r = Level.PuckRadius;
            double[] seg = segments;

            // The step is far smaller than the puck, so its centre cannot cross a wall in one step. If it
            // ever ends up inside a shape anyway (pinched in a corner), put it back on the nearest face.
            foreach (Vec[] poly in Level.Obstacles)
            {
                if (!Geometry.PointInPolygon(Pos.X, Pos.Y, poly)) continue;
                double best = double.PositiveInfinity;
                double qx = Pos.X;
                double qy = Pos.Y;
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                {
                    Vec q = Geometry.ClosestOnSegment(Pos.X, Pos.Y, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y);
                    double d = Geometry.Hypot(Pos.X - q.X, Pos.Y - q.Y);
                    if (d < best)
                    {
                        best = d;
                        qx = q.X;
                        qy = q.Y;
                    }
                }
                if (best > 1e-9)
                {
                    double nx = (qx - Pos.X) / best;
                    double ny = (qy - Pos.Y) / best;
                    Pos.X = qx + nx * r;
                    Pos.Y = qy + ny * r;
                    Bounce(nx, ny);
                }
            }

            // Resolve against every segment the puck overlaps. A corner is just the end of a segment, so
            // the same closest-point test handles faces and corners. A few passes settle the puck when it
            // touches two walls at once.
            for (int pass = 0; pass < 4; pass++)
            {
                bool touched = false;
                for (int i = 0; i < seg.Length; i += 4)
                {
                    Vec q = Geometry.ClosestOnSegment(Pos.X, Pos.Y, seg[i], seg[i + 1], seg[i + 2], seg[i + 3]);
                    double dx = Pos.X - q.X;
                    double dy = Pos.Y - q.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 >= r * r || d2 < 1e-18) continue;
                    double d = Math.Sqrt(d2);
                    double nx = dx / d;
                    double ny = dy / d;
                    Pos.X += nx * (r - d);
                    Pos.Y += ny * (r - d);
                    Bounce(nx, ny);
                    touched = true;
                }
                if (!touched) break;
            }
        }
    }
}
