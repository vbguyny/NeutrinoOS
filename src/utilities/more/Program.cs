// NeutrinoOS Phase 5 utility: more - page through text files
//
// usage: more [-n rows] [-w columns] file...
//
//   -n rows      rows per screen (default: console height - 1)
//   -w columns   line width used to count wrapped rows
//                (default: console width, 80)
//
// A long line counts for every screen row it wraps onto; a line
// longer than the remaining space is split across pages (the
// continuation starts at column 0, exactly where the terminal wraps).
//
// Keys: space/Enter show the next page, b goes back one page, q quits.
// The file is read whole (boot-volume files are small); with standard
// input redirected and no file arguments the input is printed as-is,
// because a pipe cannot deliver paging keystrokes.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.More;

/// <summary>The more utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        int pageSize = 0;
        int width = 0;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: more [-n rows] [-w columns] file...",
                    "  -n rows      rows per screen (default: console height - 1)",
                    "  -w columns   line width for counting wrapped rows",
                    "               (default: console width, 80)",
                    "",
                    "  Long lines count for every row they wrap onto; a line",
                    "  longer than the remaining space continues on the next page.",
                    "",
                    "  space/Enter  next page",
                    "  b            previous page",
                    "  q            quit");
            }
            if (a == "-n")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("more", "-n: missing row count");
                if (!Util.TryParseInt(args[i + 1], out pageSize) || pageSize <= 0)
                    return Util.Fail("more", args[i + 1] + ": invalid row count");
                i++;
            }
            else if (a == "-w")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("more", "-w: missing column count");
                if (!Util.TryParseInt(args[i + 1], out width) || width < 8)
                    return Util.Fail("more", args[i + 1] + ": invalid column count");
                i++;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("more", a + ": unknown option");
            }
            else
            {
                files.Add(a);
            }
        }

        if (pageSize == 0)
        {
            pageSize = Console.WindowHeight - 1;
            if (pageSize < 5)
                pageSize = 5;
        }
        if (width == 0)
        {
            width = Console.WindowWidth;
            if (width < 8)
                width = 80;
        }

        if (files.Count == 0)
        {
            if (!Console.IsInputRedirected)
                return Util.Fail("more", "missing file operand");
            Console.Write(Util.ReadAllStdin());
            return 0;
        }

        int rc = 0;
        for (int f = 0; f < files.Count; f++)
        {
            if (!Util.TryReadInput("more", files[f], out string text))
            {
                rc = 1;
                continue;
            }
            if (files.Count > 1)
                Console.WriteLine(":::::::: " + files[f] + " ::::::::");
            if (!Page(text, pageSize, width))
                break;      // user quit
        }
        return rc;
    }

    /// <summary>
    /// Prints <paramref name="text"/> in pages of screen rows; returns
    /// false when the user quits. Long lines are measured in wrapped
    /// rows (<paramref name="width"/> columns per row) and split across
    /// pages when they straddle the bottom of the screen.
    /// </summary>
    private static bool Page(string text, int pageSize, int width)
    {
        string[] lines = Util.SplitLines(text);

        long totalChars = 0;
        for (int i = 0; i < lines.Length; i++)
            totalChars += lines[i].Length + 1;

        // Page-start history (parallel lists; top = start of the page
        // being displayed). 'b' steps back one page, forward pages
        // overwrite the entries beyond the current depth.
        var startLines = new System.Collections.Generic.List<int>();
        var startOffsets = new System.Collections.Generic.List<int>();
        startLines.Add(0);
        startOffsets.Add(0);
        int pageTop = 0;

        long consumed = 0;      // characters of the file already shown

        while (true)
        {
            int lineIndex = startLines[pageTop];
            int charOffset = startOffsets[pageTop];

            // Reset the progress counter to where this page starts
            // before rendering it: the percentage must reflect the END
            // of the page being shown. Without the reset, re-rendering
            // a page (pressing 'b') added its characters to the counter
            // a second time and the percentage climbed while scrolling
            // backwards.
            consumed = CharsBefore(lines, lineIndex, charOffset);

            int rowsLeft = pageSize;
            while (rowsLeft > 0 && lineIndex < lines.Length)
            {
                string line = lines[lineIndex];

                if (charOffset >= line.Length)
                {
                    // Blank line (or a tail left after a split): one row.
                    Console.WriteLine();
                    long step = line.Length + 1 - charOffset;
                    consumed += step < 1 ? 1 : step;
                    rowsLeft--;
                    lineIndex++;
                    charOffset = 0;
                    continue;
                }

                int segmentRows = RowsForText(line, charOffset, width);
                if (segmentRows <= rowsLeft)
                {
                    Console.WriteLine(line.Substring(charOffset));
                    consumed += line.Length + 1 - charOffset;
                    rowsLeft -= segmentRows;
                    lineIndex++;
                    charOffset = 0;
                }
                else
                {
                    // The line wraps past this page: emit the rows that
                    // fit; the rest (already at a row boundary)
                    // continues on the next page.
                    int take = ConsumeChars(line, charOffset, rowsLeft, width);
                    Console.WriteLine(line.Substring(charOffset, take));
                    consumed += take;
                    charOffset += take;
                    rowsLeft = 0;
                }
            }

            if (lineIndex >= lines.Length)
                return true;        // everything printed; no prompt at EOF

            long percent = totalChars > 0 ? consumed * 100 / totalChars : 100;
            Console.Write("--More--(" + percent + "%)");

            var key = Console.ReadKey(true);
            char c = key.KeyChar;
            Console.WriteLine();

            if (c == 'q' || c == 'Q' || c == '\x03')
                return false;

            if (c == 'b' || c == 'B')
            {
                if (pageTop > 0)
                    pageTop--;
                continue;       // re-render; the counter resets at the top
            }

            pageTop++;
            if (pageTop < startLines.Count)
            {
                startLines[pageTop] = lineIndex;
                startOffsets[pageTop] = charOffset;
            }
            else
            {
                startLines.Add(lineIndex);
                startOffsets.Add(charOffset);
            }
        }
    }

    /// <summary>
    /// Number of screen rows the text from <paramref name="start"/> to
    /// the end of the line occupies, wrapping at <paramref name="width"/>
    /// columns (tabs advance to the next 8-column stop, matching the
    /// console drivers).
    /// </summary>
    private static int RowsForText(string text, int start, int width)
    {
        int rows = 0;
        int col = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\t')
                col = (col / 8 + 1) * 8;
            else if (c == '\b')
            {
                if (col > 0)
                    col--;
            }
            else if (c >= 0x20 && c != 0x7F)
                col++;

            while (col >= width)
            {
                col -= width;
                rows++;
            }
        }
        if (col > 0)
            rows++;
        if (rows == 0)
            rows = 1;   // an empty line still takes a row
        return rows;
    }

    /// <summary>
    /// Largest number of characters from <paramref name="start"/> that
    /// fit in <paramref name="maxRows"/> rows; stopping here leaves the
    /// continuation exactly at a row boundary.
    /// </summary>
    private static int ConsumeChars(string text, int start, int maxRows, int width)
    {
        int rows = 0;
        int col = 0;
        int consumed = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\t')
                col = (col / 8 + 1) * 8;
            else if (c == '\b')
            {
                if (col > 0)
                    col--;
            }
            else if (c >= 0x20 && c != 0x7F)
                col++;

            while (col >= width)
            {
                col -= width;
                rows++;
            }

            // Completed rows plus the partial row so far must fit.
            if (rows + (col > 0 ? 1 : 0) > maxRows)
                break;
            consumed++;
        }
        return consumed;
    }

    /// <summary>Characters of the file before the given line position.</summary>
    private static long CharsBefore(string[] lines, int lineIndex, int charOffset)
    {
        long total = charOffset;
        for (int i = 0; i < lineIndex && i < lines.Length; i++)
            total += lines[i].Length + 1;
        return total;
    }
}
