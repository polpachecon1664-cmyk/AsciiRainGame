using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AsciiRain;

// ------------------------------------------------------------
//  ASCII MMORPG "rain test" - console prototype
//  Controls: WASD / arrows = move, SPACE = attack,
//            R = toggle rain, +/- = rain amount, ESC = quit
// ------------------------------------------------------------

/// <summary>Character + 24-bit colour buffer that is flushed to the console in one write.</summary>
sealed class Frame
{
    public int W { get; }
    public int H { get; }
    private readonly char[] _ch;
    private readonly int[] _rgb;

    public Frame(int w, int h)
    {
        W = w; H = h;
        _ch = new char[w * h];
        _rgb = new int[w * h];
        Clear();
    }

    public void Clear()
    {
        Array.Fill(_ch, ' ');
        Array.Fill(_rgb, 0);
    }

    public bool IsEmpty(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H && _ch[y * W + x] == ' ';

    public void Put(int x, int y, char c, int rgb)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        _ch[y * W + x] = c;
        _rgb[y * W + x] = rgb;
    }

    public void Text(int x, int y, string s, int rgb)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] != ' ') Put(x + i, y, s[i], rgb);
    }

    public void Render(Stream stdout, StringBuilder sb)
    {
        sb.Clear();
        sb.Append("\x1b[H");
        int last = -1;
        int rows = H; // we keep H = WindowHeight-1 so the last line never scrolls
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                char c = _ch[i];
                int rgb = c == ' ' ? 0 : _rgb[i];
                if (rgb != last && c != ' ')
                {
                    sb.Append("\x1b[38;2;")
                      .Append((rgb >> 16) & 255).Append(';')
                      .Append((rgb >> 8) & 255).Append(';')
                      .Append(rgb & 255).Append('m');
                    last = rgb;
                }
                sb.Append(c);
            }
            if (y < rows - 1) sb.Append("\r\n");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}

static class Col
{
    public static int Rgb(int r, int g, int b) => (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    public static int Scale(int rgb, float f) =>
        Rgb((int)(((rgb >> 16) & 255) * f), (int)(((rgb >> 8) & 255) * f), (int)((rgb & 255) * f));
}

/// <summary>A character or monster drawn from an ASCII sprite.</summary>
sealed class Actor
{
    public string Name = "";
    public int Level;
    public int MaxHp, Hp;
    public float X, Y;            // X = centre, Y = feet
    public string[] Sprite = Array.Empty<string>();
    public int Tint;
    public float HitFlash;        // seconds of white flash after being hit
    public bool Flip;             // true = draw mirrored (facing left)

    private static char Mirror(char c) => c switch
    { '(' => ')', ')' => '(', '/' => '\\', '\\' => '/', '<' => '>', '>' => '<', '[' => ']', ']' => '[', _ => c };

    public int Width => Sprite.Max(l => l.Length);
    public int Height => Sprite.Length;

    public void Draw(Frame f)
    {
        int left = (int)MathF.Round(X) - Width / 2;
        int top = (int)MathF.Round(Y) - Height + 1;
        for (int row = 0; row < Sprite.Length; row++)
        {
            var line = Sprite[row];
            if (Flip) line = new string(line.Reverse().Select(Mirror).ToArray());
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ' ') continue;
                // heavier glyphs = brighter, gives the shaded look from the video
                float b = c switch { 'M' or '@' => 1.0f, '0' or 'Q' => 0.8f, '8' or '$' => 0.65f, _ => 0.5f };
                int rgb = HitFlash > 0 ? Col.Rgb(255, 255, 255) : Col.Scale(Tint, b);
                f.Put(left + i, top + row, c, rgb);
            }
        }
        // name label above head
        string label = Name;
        f.Text((int)MathF.Round(X) - label.Length / 2, top - 1, label, Col.Rgb(200, 200, 200));
    }
}

/// <summary>Rain made of slanted glyph streaks with ground splashes.</summary>
sealed class Rain
{
    private struct Drop { public float X, Y, Vy, LandY; public int Len; }
    private struct Splash { public int X, Y; public float Life; }

    private Drop[] _drops = Array.Empty<Drop>();
    private readonly List<Splash> _splashes = new();
    private readonly Random _rng = new();
    private float _time;

    public bool Enabled = true;
    public float Amount = 1.0f;        // 0.2 .. 3
    private int _w, _h;

    public void Resize(int w, int h)
    {
        _w = w; _h = h;
        int count = (int)(w * h / 25 * Amount);
        _drops = new Drop[count];
        for (int i = 0; i < count; i++) Respawn(ref _drops[i], true);
    }

    private void Respawn(ref Drop d, bool anywhere)
    {
        d.X = _rng.Next(-_w / 4, _w + _w / 4);
        d.Y = anywhere ? _rng.Next(0, _h) : -_rng.Next(0, 6);
        d.Vy = 18 + (float)_rng.NextDouble() * 22;   // cells per second
        d.Len = 2 + _rng.Next(0, 4);
        d.LandY = _h * 0.55f + (float)_rng.NextDouble() * (_h * 0.45f - 1);
    }

    /// <summary>Wind swings slowly over time so the streaks lean left and right.</summary>
    public float Wind => 0.6f * MathF.Sin(_time * 0.4f) + 0.25f * MathF.Sin(_time * 1.3f);

    public void Update(float dt)
    {
        _time += dt;
        if (!Enabled) return;
        float wind = Wind;
        for (int i = 0; i < _drops.Length; i++)
        {
            ref var d = ref _drops[i];
            d.Y += d.Vy * dt;
            d.X += d.Vy * wind * dt * 1.8f; // x cells are narrower than tall, so push harder
            if (d.Y >= d.LandY)
            {
                if (_splashes.Count < 600)
                    _splashes.Add(new Splash { X = (int)d.X, Y = (int)d.LandY, Life = 0.35f });
                Respawn(ref d, false);
            }
        }
        for (int i = _splashes.Count - 1; i >= 0; i--)
        {
            var s = _splashes[i];
            s.Life -= dt;
            if (s.Life <= 0) _splashes.RemoveAt(i); else _splashes[i] = s;
        }
    }

    public void Draw(Frame f)
    {
        if (!Enabled) return;
        float wind = Wind;
        float slope = wind * 1.8f;
        char streak = slope > 0.35f ? '\\' : slope < -0.35f ? '/' : '|';

        foreach (var d in _drops)
        {
            for (int k = 0; k < d.Len; k++)
            {
                int x = (int)MathF.Round(d.X - k * slope);
                int y = (int)MathF.Round(d.Y) - k;
                // rain is see-through: sometimes lets sprites show through
                if (!f.IsEmpty(x, y) && _rng.NextDouble() > 0.35) continue;
                float fade = 1f - k / (float)d.Len;
                f.Put(x, y, streak, Col.Rgb((int)(70 * fade), (int)(210 * fade) + 30, (int)(120 * fade) + 40));
            }
        }
        foreach (var s in _splashes)
        {
            char c = s.Life > 0.2f ? 'o' : '.';
            f.Put(s.X, s.Y, c, Col.Rgb(90, 200, 220));
        }
    }
}

/// <summary>An arrow flying in a straight line along one row.</summary>
sealed class Arrow
{
    public float X, Y, Vx;
    public bool Alive = true;
}

static class Program
{
    // enable ANSI escape sequences in the classic Windows console
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int h);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);

    static void EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var h = GetStdHandle(-11);
        if (GetConsoleMode(h, out uint mode))
            SetConsoleMode(h, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }

    static readonly string[] PlayerSprite =
    {
        "  .-.  \\ ",
        " (o.o)-+> ",
        " /|M|  /  ",
        "  |0|     ",
        " _/ \\_    ",
    };

    static readonly string[] SkeletonSprite =
    {
        "   _____   ",
        "  /0 0 0\\  ",
        "  \\_|_|_/  ",
        "  --|0|--  ",
        " /  |0|  \\ ",
        "    |0|    ",
        "   _/ \\_   ",
    };

    static readonly string[] OgreSprite =
    {
        "     _00000_      ",
        "   /00000000000\\  ",
        "  |0000@@@@@0000| ",
        "  |000@MMMMM@000| ",
        " /00000@MMM@00000\\",
        "|0000000000000000|",
        "|00 00MMMMMMM00 00|",
        "|00 0000@M@0000 00|",
        " \\00 0000000000 00/",
        "   00000000000000  ",
        "    000     000    ",
        "   0000     0000   ",
    };

    static Actor MakeSkeleton(float x, float y) => new()
    { Name = "Skeleton Warrior", Level = 8, MaxHp = 150, Hp = 150, X = x, Y = y, Sprite = SkeletonSprite, Tint = Col.Rgb(210, 210, 230) };

    static Actor MakeOgre(float x, float y) => new()
    { Name = "Ogre Chieftain", Level = 12, MaxHp = 420, Hp = 420, X = x, Y = y, Sprite = OgreSprite, Tint = Col.Rgb(170, 170, 215) };

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        EnableAnsi();
        Console.CursorVisible = false;
        Console.Write("\x1b[2J");

        var stdout = Console.OpenStandardOutput();
        var sb = new StringBuilder(1 << 18);
        var rng = new Random(1234);
        var rain = new Rain();
        Frame frame = null!;
        int w = 0, h = 0;

        Actor player = new() { Name = "Wanderer", Level = 1, MaxHp = 100, Hp = 100, Sprite = PlayerSprite, Tint = Col.Rgb(235, 235, 245) };
        Actor enemy = MakeOgre(0, 0);
        bool spawnedOgre = true;
        float attackCooldown = 0, enemyMove = 0;
        var arrows = new List<Arrow>();
        string status = "";
        float statusTime = 0;

        var clock = Stopwatch.StartNew();
        double prev = 0;
        bool running = true;

        try
        {
            while (running)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - prev, 0.1);
                prev = now;

                // ----- resize handling -----
                int cw = Math.Max(40, Console.WindowWidth);
                int ch = Math.Max(15, Console.WindowHeight - 1);
                if (cw != w || ch != h)
                {
                    w = cw; h = ch;
                    frame = new Frame(w, h);
                    rain.Resize(w, h);
                    Console.Write("\x1b[2J");
                    player.X = w * 0.3f; player.Y = h * 0.8f;
                    enemy.X = w * 0.65f; enemy.Y = h * 0.8f;
                }

                // ----- input -----
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    switch (key)
                    {
                        case ConsoleKey.Escape: running = false; break;
                        case ConsoleKey.A: case ConsoleKey.LeftArrow: player.X -= 2; break;
                        case ConsoleKey.D: case ConsoleKey.RightArrow: player.X += 2; break;
                        case ConsoleKey.W: case ConsoleKey.UpArrow: player.Y -= 1; break;
                        case ConsoleKey.S: case ConsoleKey.DownArrow: player.Y += 1; break;
                        case ConsoleKey.R: rain.Enabled = !rain.Enabled; break;
                        case ConsoleKey.OemPlus: case ConsoleKey.Add: rain.Amount = MathF.Min(3f, rain.Amount + 0.25f); rain.Resize(w, h); break;
                        case ConsoleKey.OemMinus: case ConsoleKey.Subtract: rain.Amount = MathF.Max(0.25f, rain.Amount - 0.25f); rain.Resize(w, h); break;
                        case ConsoleKey.Spacebar:
                            if (attackCooldown <= 0 && enemy.Hp > 0)
                            {
                                int dir = enemy.X >= player.X ? 1 : -1;
                                player.Flip = dir < 0;
                                // bow tip is on the sprite's right side (or left when flipped), on the "arrow" row
                                arrows.Add(new Arrow { X = player.X + dir * 5, Y = player.Y - player.Height + 2, Vx = dir * 70f });
                                attackCooldown = 0.5f;
                            }
                            break;
                    }
                }
                player.X = Math.Clamp(player.X, 4, w - 5);
                player.Y = Math.Clamp(player.Y, h * 0.55f, h - 2);

                // ----- update -----
                attackCooldown -= dt;
                statusTime -= dt;
                player.HitFlash -= dt;
                enemy.HitFlash -= dt;
                enemyMove -= dt;
                if (enemyMove <= 0 && MathF.Abs(enemy.X - player.X) > enemy.Width / 2 + 5)
                {
                    enemy.X += MathF.Sign(player.X - enemy.X);
                    enemyMove = 0.12f;
                }
                if (enemy.Hp <= 0)
                {
                    // respawn the other monster type
                    spawnedOgre = !spawnedOgre;
                    enemy = spawnedOgre ? MakeOgre(w * 0.7f, h * 0.8f) : MakeSkeleton(w * 0.7f, h * 0.8f);
                }
                for (int i = arrows.Count - 1; i >= 0; i--)
                {
                    var a = arrows[i];
                    a.X += a.Vx * dt;
                    int top = (int)MathF.Round(enemy.Y) - enemy.Height + 1;
                    bool inX = MathF.Abs(a.X - enemy.X) <= enemy.Width / 2f;
                    bool inY = a.Y >= top && a.Y <= enemy.Y;
                    if (enemy.Hp > 0 && inX && inY)
                    {
                        int dmg = rng.Next(10, 35);
                        enemy.Hp = Math.Max(0, enemy.Hp - dmg);
                        enemy.HitFlash = 0.12f;
                        status = $"Your arrow hits {enemy.Name} for {dmg}";
                        statusTime = 1.5f;
                        a.Alive = false;
                    }
                    if (a.X < -5 || a.X > w + 5) a.Alive = false;
                    if (!a.Alive) arrows.RemoveAt(i);
                }
                rain.Update(dt);

                // ----- draw -----
                frame.Clear();
                DrawBackground(frame, rng, now);
                // draw back-to-front by Y so closer actors overlap farther ones
                var actors = new[] { player, enemy }.OrderBy(a => a.Y);
                foreach (var a in actors) a.Draw(frame);
                foreach (var a in arrows)
                {
                    int dir = a.Vx > 0 ? 1 : -1;
                    int ax = (int)MathF.Round(a.X), ay = (int)MathF.Round(a.Y);
                    frame.Put(ax - dir * 2, ay, '-', Col.Rgb(160, 120, 70));
                    frame.Put(ax - dir, ay, '=', Col.Rgb(200, 160, 90));
                    frame.Put(ax, ay, dir > 0 ? '>' : '<', Col.Rgb(255, 255, 255));
                }
                rain.Draw(frame);
                DrawBossBar(frame, enemy);
                frame.Text(2, h - 1, statusTime > 0 ? status : "WASD move | SPACE shoot bow | R rain | +/- amount | ESC quit", Col.Rgb(120, 140, 130));

                frame.Render(stdout, sb);
                Thread.Sleep(16);
            }
        }
        finally
        {
            Console.Write("\x1b[0m\x1b[2J\x1b[H");
            Console.CursorVisible = true;
        }
    }

    /// <summary>Dim glyph "trees" in the distance and grassy ground, stable between frames.</summary>
    static void DrawBackground(Frame f, Random _, double time)
    {
        const string treeGlyphs = "$80Q#,.:'";
        const string groundGlyphs = "|\\/-=_";
        for (int y = 0; y < f.H; y++)
        {
            for (int x = 0; x < f.W; x++)
            {
                // cheap deterministic hash so the scene does not flicker
                uint hsh = (uint)(x * 73856093 ^ y * 19349663);
                hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;

                if (y < f.H * 0.45f)
                {
                    double trunk = Math.Sin(x * 0.11) + Math.Sin(x * 0.27 + 1.3);
                    if (trunk > 0.7 && hsh % 3 != 0)
                        f.Put(x, y, treeGlyphs[(int)(hsh % treeGlyphs.Length)], Col.Rgb(55, 70, 80));
                }
                else if (y > f.H * 0.55f && hsh % 4 == 0)
                {
                    float depth = (y - f.H * 0.55f) / (f.H * 0.45f);
                    f.Put(x, y, groundGlyphs[(int)(hsh % groundGlyphs.Length)],
                          Col.Rgb(25, (int)(60 + 60 * depth), (int)(40 + 30 * depth)));
                }
            }
        }
    }

    static void DrawBossBar(Frame f, Actor e)
    {
        int barW = Math.Min(f.W - 4, 60);
        int x0 = (f.W - barW) / 2;
        int frameCol = Col.Rgb(220, 220, 220);

        f.Put(x0, 0, '┌', frameCol); f.Put(x0 + barW - 1, 0, '┐', frameCol);
        f.Put(x0, 3, '└', frameCol); f.Put(x0 + barW - 1, 3, '┘', frameCol);
        for (int x = 1; x < barW - 1; x++) { f.Put(x0 + x, 0, '─', frameCol); f.Put(x0 + x, 3, '─', frameCol); }
        for (int y = 1; y <= 2; y++) { f.Put(x0, y, '│', frameCol); f.Put(x0 + barW - 1, y, '│', frameCol); }

        string title = $"{e.Name} lv {e.Level}";
        string hp = $"{e.Hp} / {e.MaxHp}";
        f.Text(x0 + 2, 1, title, Col.Rgb(230, 190, 150));
        f.Text(x0 + barW - 2 - hp.Length, 1, hp, Col.Rgb(230, 230, 230));

        int fill = (int)((barW - 2) * (e.Hp / (float)e.MaxHp));
        for (int x = 0; x < barW - 2; x++)
            f.Put(x0 + 1 + x, 2, x < fill ? '|' : '.', x < fill ? Col.Rgb(200, 70, 60) : Col.Rgb(80, 50, 50));
    }
}
