namespace Sotn;

public enum GameState { Init, Title, Play }
public enum PlayStep { Reset, Default }
public enum PlayableCharacter { Alucard, Richter }
public enum Stage { Test }
public enum PlayerStep { Stand }

public static class Game
{
    public const uint EngineStepAddr = 0x8003C9A4;
    public static bool Available => false;
    public static GameState State => GameState.Init;
    public static PlayStep EngineStep => PlayStep.Reset;
    public static bool InGame => false;
    public static Stage StageId => Stage.Test;
    public static PlayableCharacter Character => PlayableCharacter.Alucard;
    public static int Area => 0;
    public static int Room => 0;
    public static int RoomX => 0;
    public static int RoomY => 0;
    public static bool IsLoading => false;
    public static bool MenuOpen => false;
    public static bool MapOpen => false;
    public static int CameraX => 0;
    public static int CameraY => 0;
    public static ushort Pressed => 0;
    public static ushort Tapped => 0;
    public static ushort Pressed2 => 0;
    public static ushort Tapped2 => 0;
    public static string SeedName => "";
    public static string PresetName => "";
}

public static class Entities
{
    public const int Count = 256;
    public static Entity Player => new();
    public static Entity At(int slot) => new();
}

public sealed class Entity
{
    public bool IsAlive => false;
    public ushort EntityId => 0;
    public ushort EnemyId => 0;
    public int PosX => 0;
    public int PosY => 0;
    public int VelocityX => 0;
    public int VelocityY => 0;
    public ushort Step => 0;
    public ushort StepSub => 0;
    public ushort Params => 0;
    public int Flags => 0;
    public ushort HitboxState => 0;
    public short HitPoints => 0;
    public short Attack => 0;
    public byte HitboxWidth => 0;
    public byte HitboxHeight => 0;
    public uint Update => 0;
}

public static class Player
{
    public static int PosX => 0;
    public static int PosY => 0;
    public static int ScreenX => 0;
    public static int ScreenY => 0;
    public static int VelocityX => 0;
    public static int VelocityY => 0;
    public static bool FacingLeft => false;
    public static PlayerStep Step => PlayerStep.Stand;
    public static uint Status => 0;
    public static bool HasControl => false;
    public static bool IsInvincible => false;
    public static int Hp => 0;
    public static int HpMax => 0;
    public static int Mp => 0;
    public static int MpMax => 0;
    public static int Hearts => 0;
    public static int HeartsMax => 0;
    public static int Level => 0;
    public static int Exp => 0;
    public static int Gold => 0;
    public static int KillCount => 0;
}
