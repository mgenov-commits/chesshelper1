using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System.Numerics;
using System.Text;

namespace ChessHelper
{
    // ====================================================================
    //  ГЛАВНЫЙ ЭКРАН
    // ====================================================================
    [Activity(Label = "ChessHelper", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int ReqProjection = 100;
        EditText? _x, _y, _size;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var m = Resources!.DisplayMetrics!;
            var prefs = GetSharedPreferences("cfg", FileCreationMode.Private)!;

            var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            layout.SetPadding(40, 60, 40, 40);

            layout.AddView(new TextView(this)
            {
                Text = "Область доски на экране (в пикселях).\nЭкран: " + m.WidthPixels + " x " + m.HeightPixels
            });

            _x = Field(layout, "Левый край X", prefs.GetInt("x", 0).ToString());
            _y = Field(layout, "Верхний край Y", prefs.GetInt("y", (m.HeightPixels - m.WidthPixels) / 2).ToString());
            _size = Field(layout, "Размер доски (ширина)", prefs.GetInt("s", m.WidthPixels).ToString());

            var btn = new Button(this) { Text = "Запустить помощник" };
            btn.Click += (s, e) => StartHelper();
            layout.AddView(btn);

            SetContentView(layout);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 1);
        }

        EditText Field(LinearLayout parent, string hint, string value)
        {
            var e = new EditText(this) { Hint = hint, Text = value, InputType = Android.Text.InputTypes.ClassNumber };
            parent.AddView(e);
            return e;
        }

        void StartHelper()
        {
            if (!Settings.CanDrawOverlays(this))
            {
                StartActivity(new Intent(Settings.ActionManageOverlayPermission,
                    Android.Net.Uri.Parse("package:" + PackageName)));
                Android.Widget.Toast.MakeText(this, "Разрешите показ поверх других окон и нажмите кнопку снова", ToastLength.Long)?.Show();
                return;
            }
            var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqProjection);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqProjection || resultCode != Result.Ok || data == null) return;

            int bx = int.Parse(_x!.Text!), by = int.Parse(_y!.Text!), bs = int.Parse(_size!.Text!);
            var ed = GetSharedPreferences("cfg", FileCreationMode.Private)!.Edit()!;
            ed.PutInt("x", bx); ed.PutInt("y", by); ed.PutInt("s", bs); ed.Apply();

            var i = new Intent(this, typeof(CaptureService));
            i.PutExtra("code", (int)resultCode);
            i.PutExtra("data", data);
            i.PutExtra("bx", bx);
            i.PutExtra("by", by);
            i.PutExtra("bs", bs);
            StartForegroundService(i);
            MoveTaskToBack(true);
        }
    }

    // ====================================================================
    //  СТРЕЛКА ПОВЕРХ ЭКРАНА
    // ====================================================================
    class ArrowView : View
    {
        float x1, y1, x2, y2; bool has;
        readonly Paint p = new Paint { Color = Android.Graphics.Color.Red, StrokeWidth = 14, AntiAlias = true };

        public ArrowView(Context c) : base(c) { p.SetStyle(Paint.Style.FillAndStroke); }

        public void Set(float a, float b, float c, float d) { x1 = a; y1 = b; x2 = c; y2 = d; has = true; Invalidate(); }
        public void Clear() { has = false; Invalidate(); }

        protected override void OnDraw(Canvas? canvas)
        {
            if (!has || canvas == null) return;
            canvas.DrawLine(x1, y1, x2, y2, p);
            double ang = Math.Atan2(y2 - y1, x2 - x1);
            float L = 50;
            var path = new Android.Graphics.Path();
            path.MoveTo(x2, y2);
            path.LineTo(x2 - L * (float)Math.Cos(ang - 0.5), y2 - L * (float)Math.Sin(ang - 0.5));
            path.LineTo(x2 - L * (float)Math.Cos(ang + 0.5), y2 - L * (float)Math.Sin(ang + 0.5));
            path.Close();
            canvas.DrawPath(path, p);
        }
    }

    // ====================================================================
    //  STOCKFISH (с авто-перезапуском и таймаутами)
    // ====================================================================
    public class StockfishEngine : IDisposable
    {
        System.Diagnostics.Process? _p;
        string _path = "";
        readonly object _lock = new();

        public void Init(string path) { _path = path; }

        bool Alive => _p != null && !_p.HasExited;

        void Kill()
        {
            try { _p?.Kill(); } catch { }
            try { _p?.Dispose(); } catch { }
            _p = null;
        }

        void StartProcess()
        {
            Kill();
            var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _path,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            p.Start();
            _p = p;
            Send("uci");
            if (!WaitFor("uciok", 8000)) throw new Exception("Stockfish не ответил (uci)");
            Send("isready");
            if (!WaitFor("readyok", 8000)) throw new Exception("Stockfish не ответил (isready)");
        }

        void Send(string s)
        {
            _p!.StandardInput.WriteLine(s);
            _p.StandardInput.Flush();
        }

        string? ReadLine(int timeoutMs)
        {
            var t = _p!.StandardOutput.ReadLineAsync();
            if (t.Wait(timeoutMs)) return t.Result;
            return null;
        }

        bool WaitFor(string token, int timeoutMs)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                var line = ReadLine(Math.Max(100, (int)(end - DateTime.UtcNow).TotalMilliseconds));
                if (line == null) return false;
                if (line.StartsWith(token)) return true;
            }
            return false;
        }

        public string? BestMove(string fen, int ms = 1200)
        {
            lock (_lock)
            {
                try
                {
                    if (!Alive) StartProcess();
                    Send("position fen " + fen);
                    Send("go movetime " + ms);
                    var end = DateTime.UtcNow.AddMilliseconds(ms + 6000);
                    while (DateTime.UtcNow < end)
                    {
                        var line = ReadLine(Math.Max(100, (int)(end - DateTime.UtcNow).TotalMilliseconds));
                        if (line == null) break;
                        if (line.StartsWith("bestmove"))
                        {
                            var parts = line.Split(' ');
                            return parts.Length > 1 ? parts[1] : null;
                        }
                    }
                    Kill(); // завис — перезапустим при следующем запросе
                    return null;
                }
                catch
                {
                    Kill();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { if (Alive) Send("quit"); } catch { }
                Kill();
            }
        }
    }

    // ====================================================================
    //  РАСПОЗНАВАНИЕ ДОСКИ (по силуэту фигуры, не зависит от цвета клетки)
    // ====================================================================
    public class BoardRecognizer
    {
        public int X, Y, Size;
        public bool WhiteBottom = true;

        const int N = 32;
        const int MinFg = 60;      // меньше пикселей фигуры — клетка пустая
        const int Thr = 90;        // порог отличия от фона клетки

        class Feat
        {
            public ulong[] M = new ulong[16];
            public int Count;
            public double Lum;
        }

        readonly List<(char ch, Feat f)> _tpl = new();
        public bool Calibrated => _tpl.Count > 0;

        void CheckBounds(Bitmap s)
        {
            int cs = Size / 8;
            if (cs < 8) throw new Exception("Слишком маленький размер доски");
            if (X < 0 || Y < 0 || X + cs * 8 > s.Width || Y + cs * 8 > s.Height)
                throw new Exception("Доска выходит за границы экрана (проверьте X, Y, размер)");
        }

        static void Avg(int[] px, int x0, int x1, int y0, int y1, ref long r, ref long g, ref long b, ref int n)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = px[y * N + x];
                    r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++;
                }
        }

        Feat Feature(Bitmap s, int sr, int sc)
        {
            int cs = Size / 8;
            int x = X + sc * cs, y = Y + sr * cs;
            using var c = Bitmap.CreateBitmap(s, x, y, cs, cs)!;
            var sm = Bitmap.CreateScaledBitmap(c, N, N, true)!;
            var px = new int[N * N];
            sm.GetPixels(px, 0, N, 0, 0, N, N);
            if (sm.Handle != c.Handle) sm.Dispose();

            long br = 0, bg = 0, bb = 0; int bn = 0;
            Avg(px, 1, 3, 14, 17, ref br, ref bg, ref bb, ref bn);
            Avg(px, 28, 30, 14, 17, ref br, ref bg, ref bb, ref bn);
            Avg(px, 14, 17, 1, 3, ref br, ref bg, ref bb, ref bn);
            br /= bn; bg /= bn; bb /= bn;

            var f = new Feat();
            double lum = 0;
            for (int i = 0; i < N * N; i++)
            {
                int col = px[i];
                int r = (col >> 16) & 255, g = (col >> 8) & 255, b = col & 255;
                int d = Math.Abs(r - (int)br) + Math.Abs(g - (int)bg) + Math.Abs(b - (int)bb);
                if (d > Thr)
                {
                    f.M[i >> 6] |= 1UL << (i & 63);
                    f.Count++;
                    lum += 0.299 * r + 0.587 * g + 0.114 * b;
                }
            }
            f.Lum = f.Count > 0 ? lum / f.Count : 0;
            return f;
        }

        static double Dist(Feat a, Feat b)
        {
            int h = 0;
            for (int i = 0; i < 16; i++) h += BitOperations.PopCount(a.M[i] ^ b.M[i]);
            return h + Math.Abs(a.Lum - b.Lum) * 3;
        }

        static char LogicalPiece(int r, int c) => r switch
        {
            0 => "rnbqkbnr"[c],
            1 => 'p',
            6 => 'P',
            7 => "RNBQKBNR"[c],
            _ => '.'
        };

        public char[,] StartGrid()
        {
            var g = new char[8, 8];
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++) g[r, c] = LogicalPiece(r, c);
            return g;
        }

        // Возвращает null при успехе, иначе текст причины
        public string? TryCalibrate(Bitmap screen)
        {
            CheckBounds(screen);
            var feats = new Feat[8, 8];
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    feats[r, c] = Feature(screen, r, c);
                    bool filled = feats[r, c].Count >= MinFg;
                    bool shouldBeFilled = r < 2 || r > 5;
                    if (filled != shouldBeFilled) return "Не стартовая позиция";
                }

            double top = 0, bottom = 0;
            for (int c = 0; c < 8; c++)
            {
                top += feats[0, c].Lum + feats[1, c].Lum;
                bottom += feats[6, c].Lum + feats[7, c].Lum;
            }
            WhiteBottom = bottom > top;

            _tpl.Clear();
            for (int sr = 0; sr < 8; sr++)
            {
                if (sr >= 2 && sr <= 5) continue;
                for (int sc = 0; sc < 8; sc++)
                {
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    _tpl.Add((LogicalPiece(lr, lc), feats[sr, sc]));
                }
            }
            return null;
        }

        public char[,] Recognize(Bitmap screen)
        {
            CheckBounds(screen);
            var b = new char[8, 8];
            for (int sr = 0; sr < 8; sr++)
                for (int sc = 0; sc < 8; sc++)
                {
                    var f = Feature(screen, sr, sc);
                    char best = '.';
                    if (f.Count >= MinFg)
                    {
                        double bd = double.MaxValue;
                        foreach (var (ch, t) in _tpl)
                        {
                            double d = Dist(f, t);
                            if (d < bd) { bd = d; best = ch; }
                        }
                    }
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    b[lr, lc] = best;
                }
            return b;
        }

        // Грубый «отпечаток» доски для обнаружения изменений
        public int[] Signature(Bitmap s)
        {
            const int G = 64;
            var sig = new int[G * G];
            int step = Math.Max(1, Size / G);
            for (int j = 0; j < G; j++)
                for (int i = 0; i < G; i++)
                {
                    int px = Math.Min(s.Width - 1, X + i * step);
                    int py = Math.Min(s.Height - 1, Y + j * step);
                    int c = s.GetPixel(px, py);
                    sig[j * G + i] = (int)(0.299 * Android.Graphics.Color.GetRedComponent(c)
                                         + 0.587 * Android.Graphics.Color.GetGreenComponent(c)
                                         + 0.114 * Android.Graphics.Color.GetBlueComponent(c));
                }
            return sig;
        }

        public static bool Differs(int[] a, int[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 40) n++;
            return n > 10;
        }

        public static bool Valid(char[,] b)
        {
            int K = 0, k = 0;
            foreach (var ch in b) { if (ch == 'K') K++; else if (ch == 'k') k++; }
            return K == 1 && k == 1;
        }

        public static string ToFen(char[,] b, char side)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 8; r++)
            {
                int e = 0;
                for (int c = 0; c < 8; c++)
                {
                    if (b[r, c] == '.') e++;
                    else { if (e > 0) { sb.Append(e); e = 0; } sb.Append(b[r, c]); }
                }
                if (e > 0) sb.Append(e);
                if (r < 7) sb.Append('/');
            }
            string castle = "";
            if (b[7, 4] == 'K' && b[7, 7] == 'R') castle += "K";
            if (b[7, 4] == 'K' && b[7, 0] == 'R') castle += "Q";
            if (b[0, 4] == 'k' && b[0, 7] == 'r') castle += "k";
            if (b[0, 4] == 'k' && b[0, 0] == 'r') castle += "q";
            if (castle == "") castle = "-";
            sb.Append($" {side} {castle} - 0 1");
            return sb.ToString();
        }

        public (float x, float y) Center(string sq)
        {
            int file = sq[0] - 'a', rank = sq[1] - '1';
            int col = WhiteBottom ? file : 7 - file;
            int row = WhiteBottom ? 7 - rank : rank;
            float cs = Size / 8f;
            return (X + col * cs + cs / 2, Y + row * cs + cs / 2);
        }
    }

    class ProjCallback : MediaProjection.Callback { }

    // ====================================================================
    //  СЕРВИС: захват экрана, панель кнопок, автоматический режим
    // ====================================================================
    [Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaProjection)]
    public class CaptureService : Service
    {
        MediaProjection? _proj;
        ImageReader? _reader;
        VirtualDisplay? _vd;
        int _w, _h;
        IWindowManager? _wm;
        ArrowView? _arrow;
        LinearLayout? _panel;
        Bitmap? _cache;

        readonly BoardRecognizer _rec = new();
        readonly StockfishEngine _engine = new();
        readonly SemaphoreSlim _gate = new(1, 1);
        CancellationTokenSource? _cts;

        volatile bool _auto = true;
        char _manualSide = 'w';
        int[]? _lastSig;
        char[,]? _prevGrid;
        bool _pending;
        int _stable;

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent == null) { StopSelf(); return StartCommandResult.NotSticky; }

            var ch = new NotificationChannel("chess", "ChessHelper", NotificationImportance.Low);
            ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(ch);
            var n = new Notification.Builder(this, "chess")
                .SetContentTitle("ChessHelper работает")
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!.Build()!;
            StartForeground(1, n, Android.Content.PM.ForegroundService.TypeMediaProjection);

            _rec.X = intent.GetIntExtra("bx", 0);
            _rec.Y = intent.GetIntExtra("by", 0);
            _rec.Size = intent.GetIntExtra("bs", 800);

            var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            var data = (intent.GetParcelableExtra("data") as Intent)!;
            _proj = mgr.GetMediaProjection(intent.GetIntExtra("code", 0), data);
            _proj!.RegisterCallback(new ProjCallback(), new Handler(Looper.MainLooper!));

            var m = Resources!.DisplayMetrics!;
            _w = m.WidthPixels; _h = m.HeightPixels;
            _reader = ImageReader.NewInstance(_w, _h, (ImageFormatType)1, 2);
            _vd = _proj.CreateVirtualDisplay("cap", _w, _h, (int)m.DensityDpi,
                (DisplayFlags)16, _reader!.Surface, null, null);

            _engine.Init(System.IO.Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libstockfish.so"));
            Task.Run(() =>
            {
                try { _engine.BestMove("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 50); ShowToast("Stockfish готов"); }
                catch (Exception ex) { ShowToast("Ошибка Stockfish: " + ex.Message); }
            });

            ShowOverlay();
            _cts = new CancellationTokenSource();
            _ = AutoLoop(_cts.Token);
            return StartCommandResult.NotSticky;
        }

        void ShowToast(string s) =>
            new Handler(Looper.MainLooper!).Post(() =>
                Android.Widget.Toast.MakeText(this, s, ToastLength.Long)?.Show());

        // ------------------------- интерфейс -------------------------
        void ShowOverlay()
        {
            _wm = GetSystemService(WindowService).JavaCast<IWindowManager>();

            _arrow = new ArrowView(this);
            var lpA = new WindowManagerLayoutParams(-1, -1,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutInScreen,
                Format.Translucent);
            _wm!.AddView(_arrow, lpA);

            _panel = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var bAuto = new Button(this) { Text = "Авто: вкл" };
            var bAn = new Button(this) { Text = "Анализ" };
            var bCal = new Button(this) { Text = "Калибровка" };
            var bSide = new Button(this) { Text = "Ход: белых" };
            var bExit = new Button(this) { Text = "Выход" };

            bAuto.Click += (s, e) =>
            {
                _auto = !_auto;
                bAuto.Text = _auto ? "Авто: вкл" : "Авто: выкл";
            };
            bAn.Click += async (s, e) => await ManualAnalyze();
            bCal.Click += async (s, e) => await ManualCalibrate();
            bSide.Click += (s, e) =>
            {
                _manualSide = _manualSide == 'w' ? 'b' : 'w';
                bSide.Text = _manualSide == 'w' ? "Ход: белых" : "Ход: чёрных";
            };
            bExit.Click += (s, e) => StopSelf();

            foreach (var b in new[] { bAuto, bAn, bCal, bSide, bExit }) _panel.AddView(b);

            var lp = new WindowManagerLayoutParams(-2, -2,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable, Format.Translucent)
            { Gravity = GravityFlags.Top | GravityFlags.Left, X = 10, Y = 150 };
            _wm.AddView(_panel, lp);
        }

        // ------------------------- захват кадра -------------------------
        // Возвращает общий кадр-кэш. НЕ освобождать вручную.
        async Task<Bitmap?> Capture()
        {
            for (int i = 0; i < 30; i++)
            {
                Android.Media.Image? img = _reader?.AcquireLatestImage();
                if (img != null)
                {
                    try
                    {
                        var plane = img.GetPlanes()![0];
                        int wBmp = plane.RowStride / plane.PixelStride;
                        var bmp = Bitmap.CreateBitmap(wBmp, _h, Bitmap.Config.Argb8888!)!;
                        bmp.CopyPixelsFromBuffer(plane.Buffer!);
                        var old = _cache;
                        _cache = bmp;
                        old?.Recycle();
                        return _cache;
                    }
                    finally { img.Close(); }
                }
                // новых кадров нет (экран не менялся) — берём последний
                if (_cache != null && i >= 2) return _cache;
                await Task.Delay(100);
            }
            return _cache;
        }

        // ------------------------- ручные кнопки -------------------------
        async Task ManualCalibrate()
        {
            await _gate.WaitAsync();
            try
            {
                _arrow!.Clear();
                await Task.Delay(250);
                var bmp = await Capture();
                if (bmp == null) { ShowToast("Нет изображения"); return; }
                string? err = await Task.Run(() => _rec.TryCalibrate(bmp));
                if (err != null) { ShowToast(err); return; }
                _prevGrid = _rec.StartGrid();
                _lastSig = _rec.Signature(bmp);
                _pending = false; _stable = 0;
                ShowToast("Калибровка готова. Снизу: " + (_rec.WhiteBottom ? "белые" : "чёрные"));
            }
            catch (Exception ex) { ShowToast("Ошибка: " + ex.Message); }
            finally { _gate.Release(); }
        }

        async Task ManualAnalyze()
        {
            await _gate.WaitAsync();
            try
            {
                if (!_rec.Calibrated) { ShowToast("Сначала калибровка на стартовой позиции"); return; }
                await DoAnalyze(_manualSide, true);
            }
            catch (Exception ex) { ShowToast("Ошибка: " + ex.Message); }
            finally { _gate.Release(); }
        }

        // ------------------------- автоматический режим -------------------------
        async Task AutoLoop(CancellationToken ct)
        {
            try
            {
                await Task.Delay(3000, ct);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    if (!_auto) continue;
                    await _gate.WaitAsync(ct);
                    try { await Tick(); }
                    catch (System.OperationCanceledException) { throw; }
                    catch (Exception ex) { ShowToast("Ошибка: " + ex.Message); }
                    finally { _gate.Release(); }
                }
            }
            catch (System.OperationCanceledException) { }
        }

        async Task Tick()
        {
            var bmp = await Capture();
            if (bmp == null) return;

            // 1. автокалибровка: ждём стартовую позицию
            if (!_rec.Calibrated)
            {
                string? err = await Task.Run(() => _rec.TryCalibrate(bmp));
                if (err == null)
                {
                    ShowToast("Автокалибровка готова. Снизу: " + (_rec.WhiteBottom ? "белые" : "чёрные"));
                    _prevGrid = _rec.StartGrid();
                    _lastSig = _rec.Signature(bmp);
                    _pending = false; _stable = 0;
                    if (_rec.WhiteBottom) await DoAnalyze('w', false);
                }
                return;
            }

            // 2. слежение за изменениями на доске
            var sig = _rec.Signature(bmp);
            if (_lastSig == null) { _lastSig = sig; return; }

            if (BoardRecognizer.Differs(sig, _lastSig))
            {
                _lastSig = sig;
                _stable = 0;
                _pending = true;
                return;
            }

            if (_pending)
            {
                _stable++;
                if (_stable >= 2)
                {
                    _pending = false;
                    _stable = 0;
                    await DoAnalyze(null, false);
                }
            }
        }

        static char NextSide(char[,] prev, char[,] cur, char bottom)
        {
            bool w = false, b = false;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (cur[r, c] != '.' && cur[r, c] != prev[r, c])
                    {
                        if (char.IsUpper(cur[r, c])) w = true; else b = true;
                    }
            if (w && !b) return 'b';
            if (b && !w) return 'w';
            return bottom;
        }

        // Вызывается только внутри _gate
        async Task DoAnalyze(char? side, bool force)
        {
            _arrow!.Clear();
            await Task.Delay(250);
            var bmp = await Capture();
            if (bmp == null) { ShowToast("Нет изображения"); return; }

            var grid = await Task.Run(() => _rec.Recognize(bmp));
            char bottom = _rec.WhiteBottom ? 'w' : 'b';
            char s = side ?? NextSide(_prevGrid ?? _rec.StartGrid(), grid, bottom);
            _prevGrid = grid;

            if (!force && s != bottom)
            {
                _lastSig = _rec.Signature(bmp);
                return; // ход соперника — подсказка не нужна
            }

            if (!BoardRecognizer.Valid(grid))
            {
                _lastSig = _rec.Signature(bmp);
                ShowToast("Позиция не распознана (нет короля). Попробуйте ещё раз");
                return;
            }

            string fen = BoardRecognizer.ToFen(grid, s);
            string? move = await Task.Run(() => _engine.BestMove(fen, 1200));
            if (move == null || move.Length < 4 || move == "(none)")
            {
                _lastSig = _rec.Signature(bmp);
                ShowToast("Ход не найден");
                return;
            }

            var a = _rec.Center(move.Substring(0, 2));
            var b = _rec.Center(move.Substring(2, 2));
            _arrow.Set(a.x, a.y, b.x, b.y);
            ShowToast("Лучший ход: " + move);

            // запоминаем кадр уже со стрелкой, чтобы она не считалась ходом
            await Task.Delay(300);
            var b2 = await Capture();
            if (b2 != null) _lastSig = _rec.Signature(b2);
        }

        public override void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            try { if (_arrow != null) _wm?.RemoveView(_arrow); } catch { }
            try { if (_panel != null) _wm?.RemoveView(_panel); } catch { }
            try { _engine.Dispose(); } catch { }
            try { _vd?.Release(); } catch { }
            try { _proj?.Stop(); } catch { }
            try { _reader?.Close(); } catch { }
            base.OnDestroy();
        }
    }
}
