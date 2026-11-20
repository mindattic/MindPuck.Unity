// Port of MindPuck.Web/src/core/bot.ts.
using System;

namespace MindPuck.Core
{
    public sealed class BotOptions
    {
        /// <summary>Gaze error, as a standard deviation in table units. A calibrated webcam is around 0.3 to 0.6.</summary>
        public double Noise = 0.4;
        /// <summary>How slowly the gaze error wanders, in seconds.</summary>
        public double NoiseTime = 0.3;
        /// <summary>How long the eyes take to reach a new point, in seconds.</summary>
        public double Lag = 0.12;
        /// <summary>The bot gives up after this many simulated seconds.</summary>
        public double TimeLimit = 75;
        /// <summary>The speed the bot tries to travel at.</summary>
        public double Cruise = 3.2;
        public uint Seed = 1;

        /// <summary>The shared defaults (DEFAULT_BOT in TS). Treat it as read-only.</summary>
        public static readonly BotOptions Default = new BotOptions();

        /// <summary>A copy with another seed: the TS spread { ...DEFAULT_BOT, seed }.</summary>
        public BotOptions WithSeed(uint seed)
        {
            return new BotOptions
            {
                Noise = Noise,
                NoiseTime = NoiseTime,
                Lag = Lag,
                TimeLimit = TimeLimit,
                Cruise = Cruise,
                Seed = seed,
            };
        }
    }

    /// <summary>
    /// A stand-in player. It finds a route, then steers the puck along it using nothing but a gaze
    /// point, with the unsteadiness and delay of a real eye tracker. If it can win a level, a person can.
    /// </summary>
    public sealed class SolverBot
    {
        private const int DecideEvery = 4; // simulation steps between decisions: 30 a second at 120 steps a second
        private const int ReplanEvery = 60; // simulation steps between route searches: twice a second
        private const double MinStandoff = 0.8;

        /// <summary>Where the bot is looking, noise included: what the simulation is given.</summary>
        public Vec Gaze;
        /// <summary>The route it is following, for drawing.</summary>
        public Vec[] Path = new Vec[0];

        private readonly Level level;
        private readonly PhysicsParams parameters;
        private readonly BotOptions options;
        private readonly NavGrid grid;
        private readonly Rng rng;
        private Vec aim;
        private Vec eye;
        private Vec wobble;
        private Vec target;
        private int tick;

        public SolverBot(Level level, PhysicsParams parameters = null, BotOptions options = null, NavGrid grid = null)
        {
            this.level = level;
            this.parameters = parameters ?? PhysicsParams.Default;
            this.options = options ?? BotOptions.Default;
            this.grid = grid ?? new NavGrid(level);
            rng = Rng.Mulberry32(this.options.Seed);
            target = new Vec(level.Goal.X, level.Goal.Y);
            aim = FarCorner(level.Start);
            eye = new Vec(aim.X, aim.Y);
            Gaze = new Vec(aim.X, aim.Y);
        }

        /// <summary>The corner of the table farthest from a point: where to look to stop pushing.</summary>
        private Vec FarCorner(Vec from)
        {
            return new Vec(
                from.X < level.Width / 2 ? level.Width - 0.2 : 0.2,
                from.Y < level.Height / 2 ? level.Height - 0.2 : 0.2);
        }

        /// <summary>Call once per simulation step, before stepping. Returns the gaze point to apply.</summary>
        public Vec Update(PuckSim sim)
        {
            double dt = parameters.Dt;
            if (tick % ReplanEvery == 0) Replan(sim.Pos);
            if (tick % DecideEvery == 0) Decide(sim);
            tick++;

            // The eye travels toward where the bot wants to look, and the error of the tracker wanders around it.
            double follow = 1 - Math.Exp(-dt / options.Lag);
            eye.X += (aim.X - eye.X) * follow;
            eye.Y += (aim.Y - eye.Y) * follow;
            double tau = options.NoiseTime;
            double kick = options.Noise * Math.Sqrt((2 * dt) / tau);
            wobble.X += (-wobble.X / tau) * dt + kick * Rng.Gaussian(rng);
            wobble.Y += (-wobble.Y / tau) * dt + kick * Rng.Gaussian(rng);
            Gaze.X = eye.X + wobble.X;
            Gaze.Y = eye.Y + wobble.Y;
            return Gaze;
        }

        private void Replan(Vec from)
        {
            Vec[] path = grid.FindPath(from, level.Goal.Pos);
            if (path == null || path.Length == 0) return;
            Path = path;
            // Head for the farthest point on the route that can be reached in a straight line.
            Vec pick = path[Math.Min(1, path.Length - 1)];
            for (int i = path.Length - 1; i >= 1; i--)
            {
                if (grid.LineClear(from, path[i]))
                {
                    pick = path[i];
                    break;
                }
            }
            target = pick;
        }

        private void Decide(PuckSim sim)
        {
            Level lv = level;
            PhysicsParams p = parameters;
            Vec pos = sim.Pos;
            Vec vel = sim.Vel;
            double toGoal = Geometry.Hypot(lv.Goal.X - pos.X, lv.Goal.Y - pos.Y);
            double speed = Geometry.Hypot(vel.X, vel.Y);

            // Sitting on the pad: look away and let it settle.
            if (toGoal < Physics.WinRadius(lv) * 0.7 && speed < p.RestSpeed * 0.8)
            {
                aim = FarCorner(pos);
                return;
            }

            double tx = target.X - pos.X;
            double ty = target.Y - pos.Y;
            double td = Geometry.Hypot(tx, ty);
            if (td > 1e-6)
            {
                tx /= td;
                ty /= td;
            }
            bool finalLeg = Geometry.Hypot(target.X - lv.Goal.X, target.Y - lv.Goal.Y) < 0.3;
            // Cruise along the route; on the last leg, slow down so as to arrive rather than fly through.
            double wanted = finalLeg ? Math.Min(options.Cruise, 1.3 * td) : options.Cruise;
            double ax = (tx * wanted - vel.X) * 3;
            double ay = (ty * wanted - vel.Y) * 3;
            double a = Geometry.Hypot(ax, ay);
            if (a < 0.08)
            {
                aim = FarCorner(pos);
                return;
            }
            ax /= a;
            ay /= a;
            a = Math.Min(a, 18);

            // Find the standoff distance whose push matches the acceleration wanted. The push falls with
            // distance, so a bisection finds it.
            double lo = MinStandoff;
            double hi = p.RepelRange;
            if (Physics.RepelAccel(lo, lv.PuckRadius, p) <= a)
            {
                hi = lo;
            }
            else
            {
                for (int i = 0; i < 18; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (Physics.RepelAccel(mid, lv.PuckRadius, p) > a) lo = mid;
                    else hi = mid;
                }
            }
            double d = hi;

            // The gaze point sits behind the puck, opposite the way it should accelerate. It has to be on
            // the table: if the wall is closer than that, look at the edge of the wall (a harder push than
            // wanted, which the next decision corrects).
            const double edge = 0.1;
            double room = double.PositiveInfinity;
            if (ax > 1e-6) room = Math.Min(room, (pos.X - edge) / ax);
            if (ax < -1e-6) room = Math.Min(room, (pos.X - (lv.Width - edge)) / ax);
            if (ay > 1e-6) room = Math.Min(room, (pos.Y - edge) / ay);
            if (ay < -1e-6) room = Math.Min(room, (pos.Y - (lv.Height - edge)) / ay);
            if (room < d) d = Math.Max(room, 0.05);
            aim.X = pos.X - ax * d;
            aim.Y = pos.Y - ay * d;
        }
    }

    public struct SolveResult
    {
        public bool Solved;
        /// <summary>Seconds the bot took, or the time limit if it failed.</summary>
        public double Time;
    }

    public static class Solver
    {
        /// <summary>Play a level with the bot. This is the proof that the level can be won.</summary>
        public static SolveResult SolveLevel(Level level, PhysicsParams parameters = null, BotOptions options = null, NavGrid grid = null)
        {
            parameters = parameters ?? PhysicsParams.Default;
            options = options ?? BotOptions.Default;
            var sim = new PuckSim(level, parameters);
            var bot = new SolverBot(level, parameters, options, grid);
            int maxSteps = (int)Math.Ceiling(options.TimeLimit / parameters.Dt);
            for (int i = 0; i < maxSteps; i++)
            {
                sim.Step(bot.Update(sim));
                if (sim.Won) return new SolveResult { Solved = true, Time = sim.Time };
            }
            return new SolveResult { Solved = false, Time = options.TimeLimit };
        }
    }
}
