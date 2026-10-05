using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace CS2DeClip;

public class DeClipPlugin : BasePlugin
{
    public override string ModuleName => "CS2-DeClip";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Lets admins remove player clips and clip entities from the current map with !declip";

    // Clip brushes that exist as real entities and can simply be removed.
    private static readonly string[] ClipEntityNames =
    {
        "func_clip_vphysics",
    };

    // Most clips are not entities: brushes textured with toolsplayerclip / toolsclip are
    // baked into the world collision mesh on the "playerclip" interaction layer
    // (LAYER_INDEX_CONTENTS_PLAYER_CLIP, bit 4). They can't be deleted, so instead players
    // are made to stop colliding with that layer.
    //
    // Player movement traces use the pawn's m_nInteractsWith mask, on the server and (via
    // networking) in client prediction. Just clearing the bit on the pawn doesn't stick:
    // before every movement command the server rebuilds the mask as
    //     mask = (mask & 0xFFFFFFFE7FF3CFEE) | base
    // where every possible "base" constant includes the playerclip bit, and networks the
    // result. The client has no such code and simply trusts what it is sent. So the bit is
    // removed from those "base" constants in the server binary; the engine then keeps the
    // clip-free mask itself and networks it, and server and client stay in agreement.

    // The AND mask above only appears in that one place in the server binary.
    private const string MaskRebuildSignature = "EE CF F3 7F FE FF FF FF";

    // The "base" constants are loaded within a few instructions of the AND mask
    // (before it on Windows, after it on Linux).
    private const int MaskRebuildWindow = 0x100;

    // 3 on Windows, 6 on Linux (where the team variants are separate constants).
    private const int MinExpectedConstants = 3;

    private const byte SolidAndPlayerClip = 0x11;
    private const byte SolidOnly = 0x01;

    // Addresses of the low byte of each patched "base" constant.
    private readonly List<nint> _patchedConstants = new();

    public override void Load(bool hotReload)
    {
        // The patch lives in the server binary, not the map, so it has to be undone by hand
        // for clips to come back on the next map.
        RegisterListener<Listeners.OnMapStart>(_ => RestorePlayerClips());
    }

    public override void Unload(bool hotReload)
    {
        RestorePlayerClips();
    }

    // CounterStrikeSharp maps css_declip to !declip and /declip in chat.
    [ConsoleCommand("css_declip", "Removes all player clips and clip entities from the current map")]
    [RequiresPermissions("@css/generic")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnDeClipCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var removed = RemoveClipEntities();

        if (!RemovePlayerClips(out var error))
        {
            command.ReplyToCommand($" [DeClip] Could not remove player clips: {error}");
            Logger.LogError("Could not remove player clips: {Error}", error);
            return;
        }

        var admin = caller?.PlayerName ?? "Console";
        Server.PrintToChatAll($" [DeClip] {admin} removed the map clips ({removed} clip entities removed).");
        Logger.LogInformation("{Admin} ran declip: removed {Removed} clip entities, patched {Constants} mask constants",
            admin, removed, _patchedConstants.Count);
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Map entities are recreated when the round restarts.
        if (_patchedConstants.Count > 0)
            Server.NextFrame(() => RemoveClipEntities());

        return HookResult.Continue;
    }

    private static int RemoveClipEntities()
    {
        var removed = 0;
        foreach (var name in ClipEntityNames)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            {
                if (!entity.IsValid)
                    continue;

                entity.Remove();
                removed++;
            }
        }

        return removed;
    }

    private bool RemovePlayerClips(out string error)
    {
        error = "";
        if (_patchedConstants.Count > 0)
            return true;

        var anchor = NativeAPI.FindSignature(Addresses.ServerPath, MaskRebuildSignature);
        if (anchor == IntPtr.Zero)
        {
            error = "the movement mask code was not found in the server binary (game update?)";
            return false;
        }

        // Each "base" constant is SOLID | PLAYER_CLIP | WINDOW | PASS_BULLETS, optionally with
        // PLAYER | NPC and a team layer, so its low 32 bits are 11 30 (00|0C) (00|80).
        var constants = new List<nint>();
        for (var address = anchor - MaskRebuildWindow; address < anchor + MaskRebuildWindow; address++)
        {
            if (Marshal.ReadByte(address) != SolidAndPlayerClip || Marshal.ReadByte(address + 1) != 0x30)
                continue;

            var third = Marshal.ReadByte(address + 2);
            var fourth = Marshal.ReadByte(address + 3);
            if ((third == 0x00 || third == 0x0C) && (fourth == 0x00 || fourth == 0x80))
                constants.Add(address);
        }

        if (constants.Count < MinExpectedConstants)
        {
            error = $"only {constants.Count} mask constants were found near the movement mask code (game update?)";
            return false;
        }

        foreach (var address in constants)
        {
            if (!WriteCodeByte(address, SolidOnly))
            {
                error = "the server binary could not be made writable";
                RestorePlayerClips();
                return false;
            }

            _patchedConstants.Add(address);
        }

        return true;
    }

    private void RestorePlayerClips()
    {
        foreach (var address in _patchedConstants)
            WriteCodeByte(address, SolidAndPlayerClip);

        _patchedConstants.Clear();
    }

    private static bool WriteCodeByte(nint address, byte value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            const uint PageExecuteReadWrite = 0x40;
            if (!VirtualProtect(address, 1, PageExecuteReadWrite, out var oldProtection))
                return false;

            Marshal.WriteByte(address, value);
            VirtualProtect(address, 1, oldProtection, out _);
            return true;
        }

        const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;
        var pageSize = (nuint)Environment.SystemPageSize;
        var page = address & ~((nint)pageSize - 1);
        if (mprotect(page, pageSize, ProtRead | ProtWrite | ProtExec) != 0)
            return false;

        Marshal.WriteByte(address, value);
        mprotect(page, pageSize, ProtRead | ProtExec);
        return true;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtection, out uint oldProtection);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(nint address, nuint length, int protection);
}
