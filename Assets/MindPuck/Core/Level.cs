// Port of MindPuck.Web/src/core/level.ts.
namespace MindPuck.Core
{
    public struct Goal
    {
        public double X;
        public double Y;
        /// <summary>Radius of the goal pad.</summary>
        public double R;

        public Vec Pos
        {
            get { return new Vec(X, Y); }
        }
    }

    /// <summary>"Zone" is an open pad. "Cup" is a pad inside a box with one side missing.</summary>
    public enum LevelKind
    {
        Zone,
        Cup,
    }

    public sealed class Level
    {
        /// <summary>
        /// World units. The table is 16 by 9 with the origin at its top-left corner and y pointing down,
        /// so a normalized gaze point (0..1) times (16, 9) is a table position.
        /// </summary>
        public const double ArenaW = 16;
        public const double ArenaH = 9;
        public const double PuckR = 0.42;
        public const double GoalR = 1.1;

        public uint Seed;
        public int Index;
        public double Width;
        public double Height;
        public double PuckRadius;
        public Vec Start;
        public Goal Goal;
        public LevelKind Kind;
        /// <summary>For a cup, the direction its open side faces, in radians.</summary>
        public double CupAngle;
        /// <summary>Everything the puck bounces off, the walls of the cup included.</summary>
        public Vec[][] Obstacles;
        /// <summary>How long the solver took to win, in seconds. Shown to the player as the time to beat.</summary>
        public double Par;
        /// <summary>How many candidates were generated before one passed the solver.</summary>
        public int Attempts;
    }
}
