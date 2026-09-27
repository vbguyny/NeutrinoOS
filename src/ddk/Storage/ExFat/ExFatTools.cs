// NeutrinoOS Phase 10 - exFAT label and attribute helpers (Task 5 core).
//
// Small volume-level operations shared by the exfatlabel and
// exfatattrib utilities and the host tests: read/set the Volume Label
// (0x83 critical primary) and read/set file attributes through the
// driver's entry-set machinery.

using System;

namespace ProtonOS.DDK.Storage.ExFat;

/// <summary>Volume label access on a raw exFAT device.</summary>
public static class ExFatLabel
{
    /// <summary>
    /// Reads the volume label ("" when none). Read-only tolerant.
    /// </summary>
    /// <param name="device">Device holding the volume.</param>
    /// <returns>The label, or null when the volume cannot be mounted.</returns>
    public static string? Get(IBlockDevice device)
    {
        var fs = new ExFatFileSystem();
        fs.Initialize();
        if (!fs.Probe(device))
            return null;
        if (fs.Mount(device, true) != FileResult.Success)
            return null;
        string label = fs.VolumeLabel ?? "";
        fs.Unmount();
        fs.Shutdown();
        return label;
    }

    /// <summary>
    /// Sets (or clears, with "") the volume label; creates the 0x83 entry
    /// when missing. The volume must not be otherwise mounted.
    /// </summary>
    /// <param name="device">Device holding the volume.</param>
    /// <param name="label">New label (max 11 UTF-16 characters).</param>
    public static int Set(IBlockDevice device, string label)
    {
        if (label.Length > ExFatConst.MaxLabelChars)
            return -2;

        var fs = new ExFatFileSystem();
        fs.Initialize();
        if (!fs.Probe(device))
            return -1;
        var mrc = fs.Mount(device, false);
        if (mrc != FileResult.Success)
            return -1;

        int rc = (int)fs.SetVolumeLabel(label);
        fs.Unmount();
        fs.Shutdown();
        return rc;
    }
}

/// <summary>File attribute helpers on a mounted volume.</summary>
public static class ExFatAttrib
{
    /// <summary>
    /// Applies a +flag/-flag style attribute change to a path
    /// ("+r-h" = add read-only, remove hidden).
    /// </summary>
    /// <param name="device">Device holding the volume.</param>
    /// <param name="path">Mount-relative path.</param>
    /// <param name="spec">Change spec made of +x/-x tokens.</param>
    public static int Apply(IBlockDevice device, string path, string spec)
    {
        ushort set = 0;
        ushort clear = 0;
        if (!ParseSpec(spec, out set, out clear))
            return -2;

        var fs = new ExFatFileSystem();
        fs.Initialize();
        if (!fs.Probe(device))
            return -1;
        var mrc = fs.Mount(device, false);
        if (mrc != FileResult.Success)
            return -1;

        int rc = (int)fs.SetAttributes(path, clear, set);
        fs.Unmount();
        fs.Shutdown();
        return rc;
    }

    /// <summary>
    /// Parses attribute specs like "+rh", "-s", "+a-h" (a sign sets
    /// the mode for the following letters; signs can switch mid-string)
    /// into set/clear masks.
    /// </summary>
    /// <param name="spec">Spec string.</param>
    /// <param name="set">Bits to set.</param>
    /// <param name="clear">Bits to clear.</param>
    public static bool ParseSpec(string spec, out ushort set, out ushort clear)
    {
        set = 0;
        clear = 0;
        bool adding = true;
        bool haveMode = false;
        for (int i = 0; i < spec.Length; i++)
        {
            char c = spec[i];
            if (c == '+')
            {
                adding = true;
                haveMode = true;
                continue;
            }
            if (c == '-')
            {
                adding = false;
                haveMode = true;
                continue;
            }
            if (!haveMode)
                return false;
            ushort bit;
            if (c == 'r' || c == 'R') bit = ExFatConst.AttrReadOnly;
            else if (c == 'h' || c == 'H') bit = ExFatConst.AttrHidden;
            else if (c == 's' || c == 'S') bit = ExFatConst.AttrSystem;
            else if (c == 'a' || c == 'A') bit = ExFatConst.AttrArchive;
            else if (c == 'd' || c == 'D') bit = ExFatConst.AttrDirectory;
            else return false;
            if (adding)
                set |= bit;
            else
                clear |= bit;
        }
        return haveMode;
    }
}
