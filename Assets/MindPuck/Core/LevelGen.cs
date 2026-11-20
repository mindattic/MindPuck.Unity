// Port of MindPuck.Web/src/core/levelgen.ts.
using System;
using System.Collections.Generic;

namespace MindPuck.Core
{
    public static class LevelGen
    {
        public const int MaxAttempts = 60;
        /// <summary>Two obstacles either touch or leave at least this much room: the puck plus a margin.</summary>
        private const double MinGap = Level.PuckR * 2 + 0.25;
        private const double CupInner = Level.GoalR + 0.15;
        private const double CupWall = 0.22;
        /// <summary>How far the corners of the cup reach from the centre of the goal.</summary>
        private const double CupReach = (CupInner + CupWall) * 1.4142135623730951;

        /// <summary>
        /// Build level number index of the run identified by seed. Levels get harder as the index
        /// grows. Every level returned has been won by the solver bot (see Bot.cs), with an unsteady gaze,
        /// in every gaze mode, so it is known to be possible; Par, ParAttract and ParSteer are the times the bot took.
        /// </summary>
        public static Level GenerateLevel(uint seed, int index, PhysicsParams parameters = null)
        {
            parameters = parameters ?? PhysicsParams.Default;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                Level level = BuildCandidate(Rng.Mulberry32(Rng.MixSeed(seed, index, attempt)), seed, index);
                if (Verify(level, parameters, attempt)) return level;
            }
            // Nothing passed (this should not happen): fall back to an open table, which always can be won.
            Level open = BuildCandidate(Rng.Mulberry32(Rng.MixSeed(seed, index, MaxAttempts)), seed, 0);
            open.Index = index;
            open.Attempts = MaxAttempts + 1;
            Verify(open, parameters, MaxAttempts);
            return open;
        }

        /// <summary>Route check first (cheap), then the bot actually plays it. Fills in Par and Attempts.</summary>
        private static bool Verify(Level level, PhysicsParams parameters, int attempt)
        {
            var grid = new NavGrid(level);
            if (grid.FindPath(level.Start, level.Goal.Pos) == null) return false;
            // The level has to be winnable every way the game can be played.
            uint seed = Rng.MixSeed(level.Seed, level.Index, 1000 + attempt);
            SolveResult result = Solver.SolveLevel(level, parameters, BotOptions.Default.With(seed, GazeMode.Repel), grid);
            if (!result.Solved) return false;
            SolveResult pulled = Solver.SolveLevel(level, parameters, BotOptions.Default.With(seed, GazeMode.Attract), grid);
            if (!pulled.Solved) return false;
            SolveResult steered = Solver.SolveLevel(level, parameters, BotOptions.Default.With(seed, GazeMode.Steer), grid);
            if (!steered.Solved) return false;
            level.Par = Math.Floor(result.Time * 10 + 0.5) / 10;
            level.ParAttract = Math.Floor(pulled.Time * 10 + 0.5) / 10;
            level.ParSteer = Math.Floor(steered.Time * 10 + 0.5) / 10;
            level.Attempts = attempt + 1;
            return true;
        }

        /// <summary>
        /// One unverified candidate. Public so the tests can compare it with the TS generator. The order
        /// of the rng draws is the level: every statement here draws in the same order as the TS source.
        /// </summary>
        public static Level BuildCandidate(Rng rng, uint seed, int index)
        {
            int d = index;
            LevelKind kind = d >= 2 && rng.Next() < 0.5 ? LevelKind.Cup : LevelKind.Zone;
            bool flip = rng.Next() < 0.5;
            double goalMargin = kind == LevelKind.Cup ? CupReach + 0.15 : Level.GoalR + 0.35;
            Vec start;
            start.X = Rng.Range(rng, 1.3, Level.ArenaW * 0.24);
            start.Y = Rng.Range(rng, 1.3, Level.ArenaH - 1.3);
            Vec goalPos;
            goalPos.X = Rng.Range(rng, Level.ArenaW * 0.7, Level.ArenaW - goalMargin);
            goalPos.Y = Rng.Range(rng, goalMargin, Level.ArenaH - goalMargin);
            if (flip)
            {
                start.X = Level.ArenaW - start.X;
                goalPos.X = Level.ArenaW - goalPos.X;
            }

            var obstacles = new List<Vec[]>();
            double cupAngle = 0;
            if (kind == LevelKind.Cup)
            {
                // The opening faces one of eight directions. Early on it faces the puck; later it may not.
                double toward = Math.Atan2(start.Y - goalPos.Y, start.X - goalPos.X);
                double step = Math.PI / 4;
                double turn = d < 4 ? 0 : Math.Floor(Rng.Range(rng, -2, 3));
                // Math.round in JS rounds halves up, so floor(x + 0.5) rather than Math.Round.
                cupAngle = (Math.Floor(toward / step + 0.5) + turn) * step;
                obstacles.AddRange(CupWalls(goalPos, cupAngle));
            }

            // Gates: a wall across the table with one gap in it.
            int gates = d < 4 ? 0 : d < 9 ? (rng.Next() < 0.6 ? 1 : 0) : rng.Next() < 0.5 ? 2 : 1;
            for (int i = 0; i < gates; i++)
            {
                double t = gates == 1 ? Rng.Range(rng, 0.38, 0.62) : i == 0 ? Rng.Range(rng, 0.3, 0.4) : Rng.Range(rng, 0.58, 0.68);
                double x = start.X + (goalPos.X - start.X) * t;
                double half = Level.PuckR * Math.Max(1.7, 2.6 - d * 0.06);
                double gapY = Rng.Range(rng, half + 0.6, Level.ArenaH - half - 0.6);
                const double thick = 0.16;
                double top = gapY - half;
                double bottom = gapY + half;
                obstacles.Add(Geometry.RectPolygon(x, top / 2, thick, top / 2));
                obstacles.Add(Geometry.RectPolygon(x, (bottom + Level.ArenaH) / 2, thick, (Level.ArenaH - bottom) / 2));
            }

            // Loose obstacles: bars, triangles, polygons, L shapes and rocks.
            int fixedCount = obstacles.Count;
            int wanted = d == 0 ? 0 : Math.Min(1 + (int)Math.Floor(d * 0.7), 8);
            double goalClear = kind == LevelKind.Cup ? CupReach + MinGap : Level.GoalR + MinGap;
            double inset = MinGap;
            for (int n = 0; n < wanted; n++)
            {
                for (int tries = 0; tries < 24; tries++)
                {
                    double sx = Rng.Range(rng, 1.2, Level.ArenaW - 1.2);
                    double sy = Rng.Range(rng, 1.2, Level.ArenaH - 1.2);
                    Vec[] shape = RandomShape(rng, sx, sy);
                    bool outside = false;
                    foreach (Vec v in shape)
                    {
                        if (v.X < inset || v.Y < inset || v.X > Level.ArenaW - inset || v.Y > Level.ArenaH - inset)
                        {
                            outside = true;
                            break;
                        }
                    }
                    if (outside) continue;
                    if (Geometry.PointPolygonDistance(start.X, start.Y, shape) < Level.PuckR + 1) continue;
                    if (Geometry.PointPolygonDistance(goalPos.X, goalPos.Y, shape) < goalClear) continue;
                    bool fits = true;
                    for (int o = 0; fits && o < obstacles.Count; o++)
                    {
                        double gap = Geometry.PolygonDistance(shape, obstacles[o]);
                        // Loose obstacles may overlap each other, but never the cup or a gate.
                        if (gap < MinGap && (o < fixedCount || gap > 0)) fits = false;
                    }
                    if (!fits) continue;
                    obstacles.Add(shape);
                    break;
                }
            }

            return new Level
            {
                Seed = seed,
                Index = index,
                Width = Level.ArenaW,
                Height = Level.ArenaH,
                PuckRadius = Level.PuckR,
                Start = start,
                Goal = new Goal { X = goalPos.X, Y = goalPos.Y, R = Level.GoalR },
                Kind = kind,
                CupAngle = cupAngle,
                Obstacles = obstacles.ToArray(),
                Par = 0,
                ParAttract = 0,
                ParSteer = 0,
                Attempts = 0,
            };
        }

        /// <summary>Three walls around the goal, leaving the side at angle open.</summary>
        private static Vec[][] CupWalls(Vec goal, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            double h = CupInner;
            double t = CupWall;
            return new[]
            {
                Wall(goal, c, s, angle, -(h + t / 2), 0, t / 2, h + t), // back
                Wall(goal, c, s, angle, -t / 2, -(h + t / 2), h + t / 2, t / 2), // one side
                Wall(goal, c, s, angle, -t / 2, h + t / 2, h + t / 2, t / 2), // the other side
            };
        }

        private static Vec[] Wall(Vec goal, double c, double s, double angle, double lx, double ly, double hw, double hh)
        {
            return Geometry.RectPolygon(goal.X + lx * c - ly * s, goal.Y + lx * s + ly * c, hw, hh, angle);
        }

        private static Vec[] RandomShape(Rng rng, double x, double y)
        {
            double pick = rng.Next();
            double angle = Rng.Range(rng, 0, Math.PI * 2);
            if (pick < 0.34)
            {
                double hw = Rng.Range(rng, 0.8, 2.1);
                double hh = Rng.Range(rng, 0.16, 0.3);
                return Geometry.RectPolygon(x, y, hw, hh, angle);
            }
            if (pick < 0.52) return Geometry.RegularPolygon(x, y, Rng.Range(rng, 0.6, 1.1), 3, angle);
            if (pick < 0.7)
            {
                double radius = Rng.Range(rng, 0.5, 1);
                int sides = 5 + (int)Math.Floor(rng.Next() * 2);
                return Geometry.RegularPolygon(x, y, radius, sides, angle);
            }
            if (pick < 0.86)
            {
                double arm = Rng.Range(rng, 1, 1.7);
                double thick = Rng.Range(rng, 0.3, 0.42);
                return Geometry.LPolygon(x, y, arm, thick, angle);
            }
            var radii = new double[7];
            double baseRadius = Rng.Range(rng, 0.55, 1);
            for (int i = 0; i < 7; i++) radii[i] = baseRadius * Rng.Range(rng, 0.75, 1.15);
            return Geometry.BlobPolygon(x, y, radii, angle);
        }
    }
}
