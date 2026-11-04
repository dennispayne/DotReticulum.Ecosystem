using System.Text;
using LXMF;

namespace NomadNet;

public static class NomadNetPaperQr
{
    public static byte[] Png(string uri, CancellationToken cancellationToken = default) =>
        LxPaperQr.Encode(uri, cancellationToken: cancellationToken).ToPng(cancellationToken: cancellationToken);

    public static string Display(string uri, CancellationToken cancellationToken = default)
    {
        var symbol = LxPaperQr.Encode(uri, cancellationToken: cancellationToken);
        var output = new StringBuilder(symbol.Size * ((symbol.Size + 1) / 2 + 1) + 16);
        output.Append("\u001b[30;47m");
        for (var row = 0; row < symbol.Size; row += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < symbol.Size; column++)
            {
                var top = symbol[column, row];
                var bottom = row + 1 < symbol.Size && symbol[column, row + 1];
                output.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }
            output.Append('\n');
        }
        output.Append("\u001b[0m");
        return output.ToString();
    }
}
