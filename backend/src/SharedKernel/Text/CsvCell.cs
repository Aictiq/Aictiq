namespace Aictiq.SharedKernel.Text;

/// <summary>
/// One CSV cell, quoted and made inert.
///
/// Spreadsheets execute a cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c>
/// as a formula, and a work-item title is written by whoever can write work items - a
/// title of <c>=HYPERLINK(...)</c> or <c>=cmd|' /C calc'!A0</c> would run on the machine
/// of whoever opens the export. A leading apostrophe is the spreadsheet convention for
/// "this is text", and it is what every major sheet strips again on display.
/// </summary>
public static class CsvCell
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r', '\n'];

    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && Array.IndexOf(FormulaTriggers, text[0]) >= 0)
        {
            text = "'" + text;
        }

        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
