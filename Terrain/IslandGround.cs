namespace LBA2LevelEditor.Terrain;

// A ground triangle as the engine packs it (T_HALF_POLY, one U32).
internal readonly record struct IslandPolygon(uint Raw)
{
    // colour bank for flat / Gouraud triangles
    public int Bank => (int)(Raw & 15);
    // 0 none, 1 "triste", 2 flat-lit texture, 3 Gouraud-lit texture
    public int TexFlag => (int)((Raw >> 4) & 3);
    // 0 none, 1 flat, 2 Gouraud, 3 dithered
    public int PolyFlag => (int)((Raw >> 6) & 3);
    public int SampleStep => (int)((Raw >> 8) & 15);
    // what Twinsen does on it: 0 nothing, 1 water (drowns), 2 electric, 3-6 conveyor, 8 invalid, 9 lava, 11 gas, 12 shallow water ...
    public int CodeJeu => (int)((Raw >> 12) & 15);
    // the cell is cut along the 1-3 diagonal instead of 0-2 (read from the cell's first triangle)
    public bool Diagonal => ((Raw >> 16) & 1) != 0;
    public bool Col => ((Raw >> 17) & 1) != 0;
    public int TextureIndex => (int)((Raw >> 19) & 0x1FFF);

    public IslandPolygon With(int? bank = null, int? texFlag = null, int? polyFlag = null, int? sampleStep = null, int? codeJeu = null, bool? diagonal = null, bool? col = null, int? textureIndex = null)
    {
        var r = Raw;
        void Put(int shift, int bits, int value) { var mask = ((1u << bits) - 1) << shift; r = (r & ~mask) | (((uint)value << shift) & mask); }
        if (bank is { } b) Put(0, 4, b);
        if (texFlag is { } t) Put(4, 2, t);
        if (polyFlag is { } p) Put(6, 2, p);
        if (sampleStep is { } s) Put(8, 4, s);
        if (codeJeu is { } c) Put(12, 4, c);
        if (diagonal is { } d) Put(16, 1, d ? 1 : 0);
        if (col is { } k) Put(17, 1, k ? 1 : 0);
        if (textureIndex is { } i) Put(19, 13, i);
        return new IslandPolygon(r);
    }

    public static readonly string[] CodeJeuNames =
    {
        "none", "water (drowns)", "electric", "conveyor west", "conveyor east", "conveyor north", "conveyor south", "labyrinth", "invalid position",
        "lava", "10", "gas", "shallow water", "animated lava", "animated gas", "15",
    };
}

// What a paint stroke copies from the template triangle onto the cells under the brush.
[Flags]
internal enum PolygonFields
{
    None = 0,
    Texture = 1,       // texture index and flags (TexFlag, PolyFlag, Bank, SampleStep)
    GameCode = 2,      // CodeJeu (water, lava ...)
    Diagonal = 4,      // which way the cell is cut
    All = Texture | GameCode | Diagonal,
}

// Ground triangle painting: pick a triangle (eyedropper) and paint it onto cells, copy texture definitions between cubes.
internal static class IslandGround
{
    // A triangle taken from a cell: its packed polygon plus the three texture corners it points at.
    public sealed record Sample(IslandPolygon Polygon, ushort[]? Texture);

    public static Sample? Pick(IslandFile island, int gx, int gz, int half)
    {
        var cx = gx / IslandCube.Cells; var cz = gz / IslandCube.Cells;
        if (island.CubeAt(cx, cz) is not { HasPolygons: true } cube) return null;
        var x = gx % IslandCube.Cells; var z = gz % IslandCube.Cells;
        var polygon = new IslandPolygon(cube.Polygon(x, z, half));
        var index = polygon.TextureIndex;
        ushort[]? texture = index * 6 + 6 <= cube.TextureDefs.Length ? cube.TextureDefs[(index * 6)..(index * 6 + 6)] : null;
        return new Sample(polygon, texture);
    }

    // The index of a texture definition in the cube's list, adding it when the cube does not have it yet.
    public static int TextureIndexFor(IslandCube cube, ushort[] definition)
    {
        var count = cube.TextureDefs.Length / 6;
        for (var i = 0; i < count; i++)
            if (cube.TextureDefs.AsSpan(i * 6, 6).SequenceEqual(definition)) return i;
        if (count >= 0x2000) throw new InvalidOperationException("The cube already has the maximum of 8192 texture definitions.");
        var grown = new ushort[(count + 1) * 6];
        cube.TextureDefs.CopyTo(grown, 0);
        definition.CopyTo(grown, count * 6);
        cube.TextureDefs = grown;
        return count;
    }

    // Paints the sample onto the cells of a region (both triangles of each cell get the sample's triangle when `bothHalves`,
    // otherwise only the half under the pointer).
    public static int Paint(IslandFile island, IslandRegion region, Sample sample, PolygonFields fields, bool bothHalves = true)
    {
        var n = 0;
        var cells = new HashSet<(int, int)>();
        foreach (var (gx, gz, w) in region.Vertices(island))
            if (w >= 0.5) cells.Add((gx, gz));
        foreach (var (gx, gz) in cells)
        {
            var cx = gx / IslandCube.Cells; var cz = gz / IslandCube.Cells;
            if (gx >= IslandFile.GridSize || gz >= IslandFile.GridSize) continue;
            if (island.CubeAt(cx, cz) is not { HasPolygons: true } cube) continue;
            var x = gx % IslandCube.Cells; var z = gz % IslandCube.Cells;
            var textureIndex = fields.HasFlag(PolygonFields.Texture) && sample.Texture is not null ? TextureIndexFor(cube, sample.Texture) : -1;
            for (var half = 0; half < (bothHalves ? 2 : 1); half++)
            {
                var polygon = new IslandPolygon(cube.Polygon(x, z, half));
                if (fields.HasFlag(PolygonFields.Texture))
                    polygon = polygon.With(bank: sample.Polygon.Bank, texFlag: sample.Polygon.TexFlag, polyFlag: sample.Polygon.PolyFlag, sampleStep: sample.Polygon.SampleStep, textureIndex: textureIndex >= 0 ? textureIndex : polygon.TextureIndex);
                if (fields.HasFlag(PolygonFields.GameCode)) polygon = polygon.With(codeJeu: sample.Polygon.CodeJeu);
                if (fields.HasFlag(PolygonFields.Diagonal)) polygon = polygon.With(diagonal: sample.Polygon.Diagonal);
                cube.SetPolygon(x, z, half, polygon.Raw);
            }
            n++;
        }
        return n;
    }

    private static readonly int[][] HalfCorners = { new[] { 0, 1, 2 }, new[] { 2, 3, 0 }, new[] { 3, 0, 1 }, new[] { 1, 2, 3 } };
    private static readonly (int X, int Z)[] CornerOffsets = { (0, 0), (0, 1), (1, 1), (1, 0) };

    // The texture corners of triangle `half` of a cell cut along `diagonal`, for a tile of the ground atlas whose top-left pixel is
    // (x, y). Coordinates are 8.8 fixed point (pixel * 256); the retail tiles sit a hair inside the tile so the texture never
    // samples the neighbouring one.
    public static ushort[] TileDefinition(int x, int y, int width, int height, bool diagonal, int half)
    {
        var corners = HalfCorners[(diagonal ? 2 : 0) + half];
        var result = new ushort[6];
        for (var i = 0; i < 3; i++)
        {
            var (ox, oz) = CornerOffsets[corners[i]];
            var u = ox == 0 ? x * 256 + 27 : (x + width) * 256 - 14;
            var v = oz == 0 ? y * 256 + 13 : (y + height) * 256 - 14;
            result[i * 2] = (ushort)Math.Clamp(u, 0, 65535);
            result[i * 2 + 1] = (ushort)Math.Clamp(v, 0, 65535);
        }
        return result;
    }

    // Paints a tile of the ground atlas (a rectangle of the 256 x 256 picture) onto the cells of a region, both triangles of each
    // cell mapped so the tile fills the cell whichever way the cell is cut. Cells with no texture yet become lit textured cells.
    public static int PaintTile(IslandFile island, IslandRegion region, int x, int y, int width, int height)
    {
        var n = 0;
        var cells = new HashSet<(int, int)>(region.Vertices(island).Where(v => v.Weight >= 0.5).Select(v => (v.Gx, v.Gz)));
        foreach (var (gx, gz) in cells)
        {
            if (gx >= IslandFile.GridSize || gz >= IslandFile.GridSize) continue;
            if (island.CubeAt(gx / IslandCube.Cells, gz / IslandCube.Cells) is not { HasPolygons: true } cube) continue;
            var lx = gx % IslandCube.Cells; var lz = gz % IslandCube.Cells;
            var diagonal = new IslandPolygon(cube.Polygon(lx, lz, 0)).Diagonal;
            for (var half = 0; half < 2; half++)
            {
                var polygon = new IslandPolygon(cube.Polygon(lx, lz, half));
                var index = TextureIndexFor(cube, TileDefinition(x, y, width, height, diagonal, half));
                cube.SetPolygon(lx, lz, half, polygon.With(texFlag: polygon.TexFlag == 0 ? 3 : polygon.TexFlag, textureIndex: index).Raw);
            }
            n++;
        }
        return n;
    }

    // Sets only the game code (water, lava ...) of the cells of a region.
    public static int PaintGameCode(IslandFile island, IslandRegion region, int codeJeu)
    {
        var n = 0;
        var cells = new HashSet<(int, int)>(region.Vertices(island).Where(v => v.Weight >= 0.5).Select(v => (v.Gx, v.Gz)));
        foreach (var (gx, gz) in cells)
        {
            if (gx >= IslandFile.GridSize || gz >= IslandFile.GridSize) continue;
            if (island.CubeAt(gx / IslandCube.Cells, gz / IslandCube.Cells) is not { HasPolygons: true } cube) continue;
            var x = gx % IslandCube.Cells; var z = gz % IslandCube.Cells;
            for (var half = 0; half < 2; half++) cube.SetPolygon(x, z, half, new IslandPolygon(cube.Polygon(x, z, half)).With(codeJeu: codeJeu).Raw);
            n++;
        }
        return n;
    }

    // Picks the diagonal that follows the terrain best (the one whose triangles are flatter), for cells whose heights just changed.
    public static int OptimiseDiagonals(IslandFile island, IslandRegion region)
    {
        var n = 0;
        var cells = new HashSet<(int, int)>(region.Vertices(island).Select(v => (v.Gx, v.Gz)));
        foreach (var (gx, gz) in cells)
        {
            if (gx >= IslandFile.GridSize || gz >= IslandFile.GridSize) continue;
            if (island.CubeAt(gx / IslandCube.Cells, gz / IslandCube.Cells) is not { HasPolygons: true } cube) continue;
            if (island.HeightAt(gx, gz) is not { } y0 || island.HeightAt(gx, gz + 1) is not { } y1 || island.HeightAt(gx + 1, gz + 1) is not { } y2 || island.HeightAt(gx + 1, gz) is not { } y3) continue;
            // the corners are 0 (0,0), 1 (0,1), 2 (1,1), 3 (1,0); cut along the diagonal of the corners that differ least
            var diagonal = Math.Abs(y1 - y3) < Math.Abs(y0 - y2);
            var x = gx % IslandCube.Cells; var z = gz % IslandCube.Cells;
            var first = new IslandPolygon(cube.Polygon(x, z, 0));
            if (first.Diagonal == diagonal) continue;
            for (var half = 0; half < 2; half++) cube.SetPolygon(x, z, half, new IslandPolygon(cube.Polygon(x, z, half)).With(diagonal: diagonal).Raw);
            n++;
        }
        return n;
    }
}

// Decor objects: the bodies of the island's OBL placed in the cubes (positions and ZVs are in the cube's own frame, 0..32767).
internal static class IslandDecors
{
    public const int MaxPerCube = 200;

    public static IslandDecor Blank(int body, int x, int y, int z, int beta = 0, int halfSize = 512, int height = 1024)
    {
        var d = new IslandDecor(new byte[48]) { Body = body, X = x, Y = y, Z = z, Beta = beta };
        d.XMin = x - halfSize; d.XMax = x + halfSize; d.YMin = y; d.YMax = y + height; d.ZMin = z - halfSize; d.ZMax = z + halfSize;
        return d;
    }

    // The cube and cube-local position of an island world position.
    public static (IslandCube Cube, int X, int Z)? Locate(IslandFile island, double worldX, double worldZ)
    {
        var cx = (int)Math.Floor(worldX / IslandFile.CubeSize); var cz = (int)Math.Floor(worldZ / IslandFile.CubeSize);
        if (island.CubeAt(cx, cz) is not { } cube) return null;
        return (cube, (int)Math.Round(worldX - cx * (double)IslandFile.CubeSize), (int)Math.Round(worldZ - cz * (double)IslandFile.CubeSize));
    }

    // Adds a decor at an island world position (Y from the ground when `y` is null). Returns null when off the island or the cube is full.
    public static (IslandCube Cube, IslandDecor Decor)? Add(IslandFile island, int body, double worldX, double worldZ, int? y = null, IslandDecor? like = null)
    {
        if (Locate(island, worldX, worldZ) is not { } at || at.Cube.Decors.Count >= MaxPerCube) return null;
        var ground = (int)Math.Round(IslandOps.Altitude(island, worldX, worldZ) ?? 0);
        IslandDecor decor;
        if (like is not null)
        {
            decor = like.Clone();
            decor.Body = (decor.Body & ~0xFFFF) | (body & 0xFFFF);
            decor.MoveTo(at.X, y ?? ground, at.Z);
        }
        else decor = Blank(body, at.X, y ?? ground, at.Z);
        at.Cube.Decors.Add(decor);
        return (at.Cube, decor);
    }

    // Moves a decor to an island world position, into another cube's list when it crosses a cube border.
    public static bool Move(IslandFile island, IslandCube from, IslandDecor decor, double worldX, double worldZ, int? y = null)
    {
        if (Locate(island, worldX, worldZ) is not { } at) return false;
        if (at.Cube != from)
        {
            if (at.Cube.Decors.Count >= MaxPerCube) return false;
            from.Decors.Remove(decor);
            at.Cube.Decors.Add(decor);
        }
        decor.MoveTo(at.X, y ?? decor.Y, at.Z);
        return true;
    }

    public static void Remove(IslandCube cube, IslandDecor decor) => cube.Decors.Remove(decor);

    // Sets the object to rest on the ground at its position (keeping its ZV relative to it).
    public static void DropToGround(IslandFile island, IslandCube cube, IslandDecor decor)
    {
        var cell = island.CellsOf(cube.Id).FirstOrDefault();
        if (IslandOps.Altitude(island, cell.X * (double)IslandFile.CubeSize + decor.X, cell.Z * (double)IslandFile.CubeSize + decor.Z) is { } ground)
            decor.MoveTo(decor.X, (int)Math.Round(ground), decor.Z);
    }
}
