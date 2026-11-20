using System;
using MindPuck.Core;
using NUnit.Framework;

namespace MindPuck.Tests
{
    /// <summary>
    /// EditMode tests for the core port. The expected numbers marked "from TS" were printed by the
    /// TypeScript implementation (MindPuck.Web/src/core, bundled by tools/levels.mjs and run with node).
    /// </summary>
    public sealed class RngTests
    {
        [Test]
        public void Mulberry32_MatchesTheTypeScriptSequence()
        {
            // From TS: mulberry32(12345), each value times 2^32.
            uint[] expected = { 4207900869u, 1317490944u, 2079646450u, 3513001552u, 2187978186u };
            Rng rng = Rng.Mulberry32(12345);
            foreach (uint value in expected) Assert.AreEqual(value / 4294967296.0, rng.Next());

            // From TS: mulberry32(0).
            uint[] fromZero = { 1144304738u, 1416247u, 958946056u };
            rng = Rng.Mulberry32(0);
            foreach (uint value in fromZero) Assert.AreEqual(value / 4294967296.0, rng.Next());
        }

        [Test]
        public void MixSeed_MatchesTheTypeScriptValues()
        {
            // From TS: mixSeed(seed, index, attempt).
            Assert.AreEqual(73856093u, Rng.MixSeed(1, 0, 0));
            Assert.AreEqual(256854115u, Rng.MixSeed(1, 5, 3));
            Assert.AreEqual(1820018690u, Rng.MixSeed(7, 12, 1003));
            // TS takes seed | 0, so -5 and 4000000000 are the 32-bit patterns below.
            Assert.AreEqual(4014621862u, Rng.MixSeed(unchecked((uint)-5), 2, 1));
            Assert.AreEqual(2865968936u, Rng.MixSeed(4000000000u, 1, 1));
        }

        [Test]
        public void Gaussian_MatchesTheTypeScriptValues()
        {
            // From TS: two draws of gaussian(mulberry32(42)).
            Rng rng = Rng.Mulberry32(42);
            Assert.AreEqual(0.9900301019167781, Rng.Gaussian(rng), 1e-12);
            Assert.AreEqual(-0.6938957720882922, Rng.Gaussian(rng), 1e-12);
        }

        [Test]
        public void Range_StaysInsideItsBounds()
        {
            Rng rng = Rng.Mulberry32(99);
            for (int i = 0; i < 1000; i++)
            {
                double v = Rng.Range(rng, -2, 3);
                Assert.GreaterOrEqual(v, -2);
                Assert.Less(v, 3);
            }
        }
    }

    public sealed class GeometryTests
    {
        [Test]
        public void PointInPolygon_HandlesTheConcaveL()
        {
            Vec[] l = Geometry.LPolygon(0, 0, 2, 0.5);
            // The arms are inside; the notch between them is not.
            Assert.IsTrue(Geometry.PointInPolygon(-0.8, -0.8, l));
            Assert.IsTrue(Geometry.PointInPolygon(0.8, -0.8, l));
            Assert.IsTrue(Geometry.PointInPolygon(-0.8, 0.8, l));
            Assert.IsFalse(Geometry.PointInPolygon(0.5, 0.5, l));
        }

        [Test]
        public void PolygonDistance_MeasuresTheGap()
        {
            Vec[] a = Geometry.RectPolygon(0, 0, 1, 1);
            Vec[] b = Geometry.RectPolygon(3, 0, 1, 1);
            Assert.AreEqual(1, Geometry.PolygonDistance(a, b), 1e-12);
            Assert.AreEqual(0, Geometry.PolygonDistance(a, Geometry.RectPolygon(1.5, 0.3, 1, 1)), 1e-12);
            Assert.AreEqual(0.5, Geometry.PointPolygonDistance(1.5, 0, a), 1e-12);
            Assert.AreEqual(0, Geometry.PointPolygonDistance(0.2, 0.2, a), 1e-12);
        }
    }

    public sealed class PhysicsTests
    {
        private static Level OpenLevel(params Vec[][] obstacles)
        {
            return new Level
            {
                Seed = 1,
                Index = 0,
                Width = Level.ArenaW,
                Height = Level.ArenaH,
                PuckRadius = Level.PuckR,
                Start = new Vec(2, 4.5),
                Goal = new Goal { X = 13, Y = 4.5, R = Level.GoalR },
                Kind = LevelKind.Zone,
                Obstacles = obstacles,
            };
        }

        [Test]
        public void RepelAccel_IsZeroAtTheCentreAndPastTheRange()
        {
            PhysicsParams p = PhysicsParams.Default;
            Assert.AreEqual(0, Physics.RepelAccel(0, Level.PuckR, p));
            Assert.AreEqual(0, Physics.RepelAccel(p.RepelRange, Level.PuckR, p));
            Assert.Greater(Physics.RepelAccel(1, Level.PuckR, p), Physics.RepelAccel(3, Level.PuckR, p));
        }

        [Test]
        public void Gaze_PushesThePuckAway()
        {
            var sim = new PuckSim(OpenLevel());
            sim.Mode = GazeMode.Repel;
            // Looking just left of the puck sends it right.
            for (int i = 0; i < 30; i++) sim.Step(new Vec(1, 4.5));
            Assert.Greater(sim.Vel.X, 0);
            Assert.AreEqual(0, sim.Vel.Y, 1e-9);
            Assert.Greater(sim.Pos.X, 2);
        }

        [Test]
        public void PullAccel_RisesLikeASpringThenHoldsSteady()
        {
            PhysicsParams p = PhysicsParams.Default;
            Assert.AreEqual(0, Physics.PullAccel(0, p));
            Assert.AreEqual(p.PullStrength / 2, Physics.PullAccel(p.PullSoftness / 2, p), 1e-12);
            Assert.AreEqual(p.PullStrength, Physics.PullAccel(p.PullSoftness, p), 1e-12);
            Assert.AreEqual(p.PullStrength, Physics.PullAccel(10, p), 1e-12);
        }

        [Test]
        public void NewSim_IsMagneticAndTheBotDefaultsToRepel()
        {
            Assert.AreEqual(GazeMode.Attract, new PuckSim(OpenLevel()).Mode);
            Assert.AreEqual(GazeMode.Repel, BotOptions.Default.Mode);
            Assert.AreEqual(GazeMode.Attract, BotOptions.Default.With(5, GazeMode.Attract).Mode);
        }

        [Test]
        public void Attract_PullsThePuckToTheGazeAndSettlesThere()
        {
            var sim = new PuckSim(OpenLevel());
            sim.Mode = GazeMode.Attract;
            var gaze = new Vec(6, 3);
            sim.Step(gaze);
            // The first push points from the puck (2, 4.5) toward the gaze point.
            Assert.Greater(sim.Push.X, 0);
            Assert.Less(sim.Push.Y, 0);
            for (int i = 0; i < 120 * 6; i++) sim.Step(gaze);
            Assert.Less(Geometry.Dist(sim.Pos, gaze), 0.1);
            Assert.Less(Geometry.Hypot(sim.Vel.X, sim.Vel.Y), 0.2);
        }

        [Test]
        public void Attract_AddsPullDragOnlyWhileAGazeIsPresent()
        {
            PhysicsParams p = PhysicsParams.Default;
            // The gaze sits on the puck, so there is no pull, only the extra drag.
            var held = new PuckSim(OpenLevel());
            held.Vel = new Vec(5, 0);
            held.Step(new Vec(2, 4.5));
            Assert.AreEqual(5 * Math.Exp(-(p.Drag + p.PullDrag) * p.Dt) - p.Friction * p.Dt, held.Vel.X, 1e-9);

            var free = new PuckSim(OpenLevel());
            free.Vel = new Vec(5, 0);
            free.Step(null);
            Assert.AreEqual(5 * Math.Exp(-p.Drag * p.Dt) - p.Friction * p.Dt, free.Vel.X, 1e-9);
        }

        [Test]
        public void SteerAccel_IsZeroAtTheCentreFullAtTheEdgeAndFadesFarAway()
        {
            PhysicsParams p = PhysicsParams.Default;
            double r = Level.PuckR;
            Assert.AreEqual(0, Physics.SteerAccel(0, r, p));
            Assert.AreEqual(0, Physics.SteerAccel(p.DeadZone * r, r, p));
            Assert.AreEqual(p.SteerStrength / 2, Physics.SteerAccel((p.DeadZone + p.SteerFull) / 2 * r, r, p), 1e-9);
            Assert.AreEqual(p.SteerStrength, Physics.SteerAccel(p.SteerFull * r, r, p), 1e-12);
            Assert.AreEqual(p.SteerStrength, Physics.SteerAccel(p.SteerRange * r, r, p), 1e-12);
            Assert.AreEqual(p.SteerStrength / 2, Physics.SteerAccel(p.SteerRange * 1.25 * r, r, p), 1e-9);
            Assert.AreEqual(0, Physics.SteerAccel(p.SteerRange * 1.5 * r, r, p), 1e-9);
            Assert.AreEqual(0, Physics.SteerAccel(10, r, p), 1e-9);
        }

        [Test]
        public void Steer_DrivesThePuckTowardTheSideLookedAt()
        {
            PhysicsParams p = PhysicsParams.Default;
            var sim = new PuckSim(OpenLevel());
            sim.Mode = GazeMode.Steer;
            // Looking just past the right edge of the puck, between SteerFull and SteerRange radii out,
            // sends it right at full strength.
            sim.Step(new Vec(2 + Level.PuckR * (p.SteerFull + p.SteerRange) / 2, 4.5));
            Assert.AreEqual(p.SteerStrength, sim.Push.X, 1e-9);
            Assert.AreEqual(0, sim.Push.Y, 1e-12);
            Assert.Greater(sim.Vel.X, 0);

            // Looking straight at the puck, or far away from it, does not drive it.
            var still = new PuckSim(OpenLevel());
            still.Mode = GazeMode.Steer;
            still.Step(new Vec(2, 4.5));
            still.Step(new Vec(12, 4.5));
            Assert.AreEqual(0, still.Vel.X);
            Assert.AreEqual(0, still.Vel.Y);
        }

        [Test]
        public void Steer_AddsSteerDragWhileAGazeIsPresent()
        {
            PhysicsParams p = PhysicsParams.Default;
            var sim = new PuckSim(OpenLevel());
            sim.Mode = GazeMode.Steer;
            sim.Vel = new Vec(5, 0);
            sim.Step(new Vec(2, 4.5));
            Assert.AreEqual(5 * Math.Exp(-(p.Drag + p.SteerDrag) * p.Dt) - p.Friction * p.Dt, sim.Vel.X, 1e-9);
        }

        [Test]
        public void NoGaze_LeavesTheRestingPuckAlone()
        {
            var sim = new PuckSim(OpenLevel());
            for (int i = 0; i < 120; i++) sim.Step(null);
            Assert.AreEqual(2, sim.Pos.X, 1e-12);
            Assert.AreEqual(4.5, sim.Pos.Y, 1e-12);
            Assert.AreEqual(1.0, sim.Time, 1e-9);
        }

        [Test]
        public void Puck_BouncesOffTheArenaEdge()
        {
            PhysicsParams p = PhysicsParams.Default;
            var sim = new PuckSim(OpenLevel());
            sim.Pos = new Vec(0.43, 4.5);
            sim.Vel = new Vec(-5, 0);
            sim.Step(null);

            // One step of drag and friction, then the wall returns the restitution share of the speed.
            double speed = 5 * Math.Exp(-p.Drag * p.Dt) - p.Friction * p.Dt;
            Assert.AreEqual(Level.PuckR, sim.Pos.X, 1e-12);
            Assert.AreEqual(speed * p.Restitution, sim.Vel.X, 1e-9);
            Assert.AreEqual(speed, sim.Impact, 1e-9);
        }

        [Test]
        public void Puck_BouncesOffAnObstacleFace()
        {
            // A bar whose left face is at x = 7.5.
            var sim = new PuckSim(OpenLevel(Geometry.RectPolygon(8, 4.5, 0.5, 2)));
            sim.Pos = new Vec(7.07, 4.5);
            sim.Vel = new Vec(5, 0);
            sim.Step(null);
            Assert.Less(sim.Vel.X, 0);
            Assert.Greater(sim.Impact, 0);
            Assert.AreEqual(7.5 - Level.PuckR, sim.Pos.X, 1e-9);
        }

        [Test]
        public void Puck_WinsByRestingOnThePad()
        {
            var sim = new PuckSim(OpenLevel());
            sim.Pos = new Vec(13, 4.5);
            // The dwell is half a second: 60 steps. Not yet at 50, certainly by 62.
            for (int i = 0; i < 50; i++) sim.Step(null);
            Assert.IsFalse(sim.Won);
            for (int i = 0; i < 12; i++) sim.Step(null);
            Assert.IsTrue(sim.Won);

            // Advance runs whole fixed steps and keeps the remainder for next time.
            sim.Reset();
            int steps = sim.Advance(0.05, null) + sim.Advance(0.05, null) + sim.Advance(0.05, null);
            Assert.That(steps, Is.InRange(17, 18));
            Assert.AreEqual(steps * sim.Params.Dt, sim.Time, 1e-9);
        }
    }

    public sealed class NavGridTests
    {
        [Test]
        public void FindPath_GoesThroughTheGapInAWall()
        {
            // A wall across the table at x = 8 with a gap between y = 3.5 and y = 5.5.
            var level = new Level
            {
                Seed = 1,
                Width = Level.ArenaW,
                Height = Level.ArenaH,
                PuckRadius = Level.PuckR,
                Start = new Vec(2, 1.5),
                Goal = new Goal { X = 13, Y = 1.5, R = Level.GoalR },
                Obstacles = new[]
                {
                    Geometry.RectPolygon(8, 1.75, 0.16, 1.75),
                    Geometry.RectPolygon(8, 7.25, 0.16, 1.75),
                },
            };
            var grid = new NavGrid(level);
            Assert.AreEqual(64, grid.Cols);
            Assert.AreEqual(36, grid.Rows);
            Assert.IsFalse(grid.LineClear(level.Start, level.Goal.Pos));

            Vec[] path = grid.FindPath(level.Start, level.Goal.Pos);
            Assert.IsNotNull(path);
            Assert.Greater(path.Length, 2);
            Assert.AreEqual(13, path[path.Length - 1].X, 1e-12);
            Assert.AreEqual(1.5, path[path.Length - 1].Y, 1e-12);
            Assert.Less(Geometry.Dist(path[0], level.Start), NavGrid.Cell);

            bool crossed = false;
            for (int i = 0; i < path.Length; i++)
            {
                Assert.IsTrue(grid.IsFree(path[i].X, path[i].Y), "point " + i + " is blocked");
                if (i > 0) Assert.LessOrEqual(Geometry.Dist(path[i - 1], path[i]), NavGrid.Cell * 1.4143 + 0.25);
                if (Math.Abs(path[i].X - 8) < 0.3)
                {
                    crossed = true;
                    Assert.Greater(path[i].Y, 3.5);
                    Assert.Less(path[i].Y, 5.5);
                }
            }
            Assert.IsTrue(crossed, "the route never passed the wall");
        }

        [Test]
        public void FindPath_ReturnsNullWhenTheTableIsCutInTwo()
        {
            var level = new Level
            {
                Seed = 1,
                Width = Level.ArenaW,
                Height = Level.ArenaH,
                PuckRadius = Level.PuckR,
                Start = new Vec(2, 4.5),
                Goal = new Goal { X = 13, Y = 4.5, R = Level.GoalR },
                Obstacles = new[] { Geometry.RectPolygon(8, 4.5, 0.2, 4.5) },
            };
            Assert.IsNull(new NavGrid(level).FindPath(level.Start, level.Goal.Pos));
        }
    }

    public sealed class LevelGenTests
    {
        // From TS: generateLevel(seed, index) for levels that passed on the first candidate, so these are
        // the values of buildCandidate at attempt 0: kind, obstacle count, start, goal, cup angle.
        private static readonly object[] Candidates =
        {
            new object[] { 1u, 0, LevelKind.Zone, 0, 12.951920395977794, 2.478113678097725, 3.6134711551945653, 5.218635156820528, 0.0 },
            new object[] { 1u, 3, LevelKind.Cup, 6, 13.079022862664424, 6.219724480807781, 4.045758255473762, 3.4177663529480253, 0.0 },
            new object[] { 1u, 6, LevelKind.Cup, 9, 3.440719961840659, 1.7651655972003937, 11.851407476309038, 4.641347756816325, -1.5707963267948966 },
            new object[] { 1u, 10, LevelKind.Zone, 10, 13.471633277232758, 1.4297184243798255, 4.005552178528161, 4.258290293812752, 0.0 },
            new object[] { 7u, 5, LevelKind.Cup, 9, 13.668070158055052, 3.261097550392151, 4.676108999385976, 4.406964151369285, 0.7853981633974483 },
        };

        [TestCaseSource(nameof(Candidates))]
        public void BuildCandidate_MatchesTheTypeScriptGenerator(
            uint seed, int index, LevelKind kind, int obstacles, double startX, double startY, double goalX, double goalY, double cupAngle)
        {
            Level level = LevelGen.BuildCandidate(Rng.Mulberry32(Rng.MixSeed(seed, index, 0)), seed, index);
            Assert.AreEqual(kind, level.Kind);
            Assert.AreEqual(obstacles, level.Obstacles.Length);
            Assert.AreEqual(startX, level.Start.X, 1e-9);
            Assert.AreEqual(startY, level.Start.Y, 1e-9);
            Assert.AreEqual(goalX, level.Goal.X, 1e-9);
            Assert.AreEqual(goalY, level.Goal.Y, 1e-9);
            Assert.AreEqual(cupAngle, level.CupAngle, 1e-9);
        }

        [Test]
        public void GeneratedLevels_AreSolvable([Values(1u, 7u)] uint seed, [Range(0, 7)] int index)
        {
            Level level = LevelGen.GenerateLevel(seed, index);
            Assert.AreEqual(seed, level.Seed);
            Assert.AreEqual(index, level.Index);
            Assert.GreaterOrEqual(level.Attempts, 1);
            Assert.LessOrEqual(level.Attempts, LevelGen.MaxAttempts, "fell back to the open table");
            Assert.Greater(level.Par, 0);

            // A route exists, and the bot wins again with the noise seed the generator proved it with.
            var grid = new NavGrid(level);
            Assert.IsNotNull(grid.FindPath(level.Start, level.Goal.Pos));
            BotOptions options = BotOptions.Default.WithSeed(Rng.MixSeed(level.Seed, level.Index, 1000 + level.Attempts - 1));
            SolveResult result = Solver.SolveLevel(level, PhysicsParams.Default, options, grid);
            Assert.IsTrue(result.Solved);
            Assert.AreEqual(level.Par, result.Time, 0.051);

            // The same in magnetic mode, which has its own par.
            Assert.Greater(level.ParAttract, 0);
            SolveResult pulled = Solver.SolveLevel(level, PhysicsParams.Default, options.With(options.Seed, GazeMode.Attract), grid);
            Assert.IsTrue(pulled.Solved);
            Assert.AreEqual(level.ParAttract, pulled.Time, 0.051);
            Assert.AreEqual(level.ParAttract, level.ParFor(GazeMode.Attract));

            // And in steer mode.
            Assert.Greater(level.ParSteer, 0);
            SolveResult steered = Solver.SolveLevel(level, PhysicsParams.Default, options.With(options.Seed, GazeMode.Steer), grid);
            Assert.IsTrue(steered.Solved);
            Assert.AreEqual(level.ParSteer, steered.Time, 0.051);
            Assert.AreEqual(level.ParSteer, level.ParFor(GazeMode.Steer));
            Assert.AreEqual(level.Par, level.ParFor(GazeMode.Repel));
        }

        [Test]
        public void GenerateLevel_IsDeterministic()
        {
            Level a = LevelGen.GenerateLevel(3, 4);
            Level b = LevelGen.GenerateLevel(3, 4);
            Assert.AreEqual(a.Par, b.Par);
            Assert.AreEqual(a.ParAttract, b.ParAttract);
            Assert.AreEqual(a.ParSteer, b.ParSteer);
            Assert.AreEqual(a.Attempts, b.Attempts);
            Assert.AreEqual(a.Obstacles.Length, b.Obstacles.Length);
            Assert.AreEqual(a.Start.X, b.Start.X);
            Assert.AreEqual(a.Goal.Y, b.Goal.Y);
        }

        [Test]
        public void Obstacles_KeepClearOfTheStartAndTheGoal()
        {
            for (int index = 0; index < 12; index++)
            {
                Level level = LevelGen.BuildCandidate(Rng.Mulberry32(Rng.MixSeed(5, index, 0)), 5, index);
                foreach (Vec[] polygon in level.Obstacles)
                {
                    Assert.Greater(Geometry.PointPolygonDistance(level.Start.X, level.Start.Y, polygon), Level.PuckR);
                    Assert.Greater(Geometry.PointPolygonDistance(level.Goal.X, level.Goal.Y, polygon), Level.GoalR);
                }
            }
        }
    }
}
