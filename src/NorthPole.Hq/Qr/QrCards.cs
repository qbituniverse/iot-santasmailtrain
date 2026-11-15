using System.Text;
using Microsoft.Extensions.Options;
using QRCoder;

namespace NorthPole.Hq.Qr;

public sealed record LegoPart(string Name, string DesignId, int Length, int Count);

public sealed record LegoPlan(int Baseplate, int CodeModules, int Offset, int TileCount, IReadOnlyList<LegoPart> Parts, string Svg);

/// <param name="Kind">"wish" for a child's wish card, "driver" for the train driver's card, which has no code to renew.</param>
public sealed record QrCard(string ChildId, string Name, string Url, int Version, string QrSvg, LegoPlan Lego, string Kind = "wish");

/// <summary>
/// Makes each child's QR code twice: as a printable card, and as a LEGO mosaic build plan
/// (black 1×N tiles on a white 32×32 baseplate) that phones can scan just like the paper one.
///
/// The trick that makes LEGO possible: the URL is written in UPPER CASE, which lets the QR code use its compact
/// "alphanumeric" mode. http://np.local/W/K7Q2 fits in a version 1 code (21×21 modules) - small enough to build.
/// </summary>
public sealed class QrCards(IOptions<HqOptions> options)
{
    // Black tiles by length, longest first. BrickLink design IDs.
    static readonly (int Length, string DesignId)[] Tiles =
    [
        (8, "4162"), (6, "6636"), (4, "2431"), (3, "63864"), (2, "3069b"), (1, "3070b"),
    ];

    readonly HqOptions _o = options.Value;

    public string WishUrl(string code) => $"{_o.Qr.BaseUrl.TrimEnd('/')}/W/{code}".ToUpperInvariant();

    /// <summary>The train driver's page. HTTP://NP.LOCAL/DRIVER still fits a version 1 code, at the lower error correction.</summary>
    public string DriverUrl => $"{_o.Qr.BaseUrl.TrimEnd('/')}/DRIVER".ToUpperInvariant();

    public QrCard Build(ChildOptions child, string code) => Card(child.Id, child.Name, WishUrl(code), "wish");

    /// <summary>The card that opens the train driver's page: the same for everyone, so there's no code to renew.</summary>
    public QrCard BuildDriver() => Card("driver", "The Train Driver", DriverUrl, "driver");

    QrCard Card(string id, string name, string url, string kind)
    {
        var data = SmallestCode(url);
        var qrSvg = new SvgQRCode(data).GetGraphic(8);
        var lego = BuildLegoPlan(data, _o.Qr.BaseplateStuds);
        return new QrCard(id, name, url, data.Version, qrSvg, lego, kind);
    }

    /// <summary>Tries both error-correction levels and keeps whichever needs fewer modules (fewer bricks).</summary>
    static QRCodeData SmallestCode(string text)
    {
        var medium = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var low = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.L);
        return low.Version < medium.Version ? low : medium;
    }

    public static LegoPlan BuildLegoPlan(QRCodeData data, int baseplate)
    {
        // QRCoder adds a 4-module quiet zone around the code; we strip it and centre the code on the baseplate.
        var matrix = data.ModuleMatrix;
        const int pad = 4;
        var size = matrix.Count - 2 * pad;
        var offset = Math.Max(0, (baseplate - size) / 2);
        if (size + 2 > baseplate)
            throw new InvalidOperationException(
                $"This QR code is {size}×{size} modules and won't fit a {baseplate}-stud baseplate. Use a shorter Qr:BaseUrl.");

        var placements = new List<(int Row, int Col, int Length)>();
        for (var r = 0; r < size; r++)
        {
            var c = 0;
            while (c < size)
            {
                if (!matrix[r + pad][c + pad]) { c++; continue; }
                var start = c;
                while (c < size && matrix[r + pad][c + pad]) c++;
                var run = c - start;
                var col = start;
                foreach (var (length, _) in Tiles)
                {
                    while (run >= length)
                    {
                        placements.Add((r, col, length));
                        col += length;
                        run -= length;
                    }
                }
            }
        }

        var parts = Tiles
            .Select(t => new LegoPart($"Tile 1 x {t.Length}, black", t.DesignId, t.Length, placements.Count(p => p.Length == t.Length)))
            .Where(p => p.Count > 0)
            .ToList();

        return new LegoPlan(baseplate, size, offset, placements.Count, parts, RenderPlan(baseplate, offset, placements));
    }

    static string RenderPlan(int baseplate, int offset, List<(int Row, int Col, int Length)> tiles)
    {
        const int s = 14;      // pixels per stud
        const int margin = 22; // room for coordinates
        var px = baseplate * s + margin * 2;
        var svg = new StringBuilder();
        svg.Append($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {px} {px}" role="img" aria-label="LEGO QR mosaic build plan">""");
        svg.Append($"""<rect x="{margin}" y="{margin}" width="{baseplate * s}" height="{baseplate * s}" rx="4" fill="#ffffff" stroke="#c9c9c9"/>""");

        for (var r = 0; r < baseplate; r++)
            for (var c = 0; c < baseplate; c++)
                svg.Append($"""<circle cx="{margin + c * s + s / 2.0}" cy="{margin + r * s + s / 2.0}" r="{s * 0.3}" fill="none" stroke="#e4e4e4"/>""");

        // Every 4 studs, a coordinate to count from (row/column numbers start at 1 like a builder would count).
        for (var i = 0; i < baseplate; i += 4)
        {
            var at = margin + i * s + s / 2.0;
            svg.Append($"""<text x="{at}" y="{margin - 7}" font-size="9" text-anchor="middle" fill="#888">{i + 1}</text>""");
            svg.Append($"""<text x="{margin - 6}" y="{at + 3}" font-size="9" text-anchor="end" fill="#888">{i + 1}</text>""");
        }

        foreach (var (row, col, length) in tiles)
        {
            var x = margin + (offset + col) * s + 0.8;
            var y = margin + (offset + row) * s + 0.8;
            svg.Append($"""<rect x="{x}" y="{y}" width="{length * s - 1.6}" height="{s - 1.6}" rx="2" fill="#1b1b1b"/>""");
            if (length > 1)
                svg.Append($"""<text x="{x + length * s / 2.0}" y="{y + s / 2.0 + 3}" font-size="8" text-anchor="middle" fill="#9a9a9a">{length}</text>""");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }
}
