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
using System.Text;

namespace ChessHelper
{
    // ======================= ЛОГ ОШИБОК =======================
    static class AppLog
    {
        static readonly object L = new();
        static bool _hooked;
        static string P => System.IO.Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "log.txt");

        public static void Write(string s)
        {
            try { lock (L) { System.IO.File.AppendAllText(P, DateTime.Now.ToString("HH:mm:ss") + " " + s + "\n"); } } catch { }
        }

        public static string Read()
        {
            try
            {
                if (!System.IO.File.Exists(P)) return "";
                var t = System.IO.File.ReadAllText(P);
                return t.Length > 1500 ? t.Substring(t.Length - 1500) : t;
            }
            catch { return ""; }
        }

        public static void Clear()
        {
            try { System.IO.File.Delete(P); } catch { }
        }

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("CRASH: " + e.ExceptionObject);
            AndroidEnvironment.UnhandledExceptionRaiser += (s, e) => Write("CRASH: " + e.Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => { Write("TASK: " + e.Exception); e.SetObserved(); };
        }
    }

    // ======================= STOCKFISH =======================
    class StockfishEngine : IDisposable
    {
        System.Diagnostics.Process? _p;
        string _path = "";

        public void Init(string path)
        {
            lock (this)
            {
                _path = path;
                Launch();
            }
        }

        void Launch()
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
            WaitFor("uciok", 10000);
            Send("setoption name Threads value 2");
            Send("isready");
            WaitFor("readyok", 10000);
        }

        void Kill()
        {
            try { _p?.Kill(); } catch { }
            try { _p?.Dispose(); } catch { }
            _p = null;
        }

        void Send(string s)
        {
            _p!.StandardInput.WriteLine(s);
            _p.StandardInput.Flush();
        }

        string? ReadLineTimeout(int ms)
        {
            var t = _p!.StandardOutput.ReadLineAsync();
            if (!t.Wait(ms)) throw new TimeoutException("Stockfish не отвечает");
            return t.Result;
        }

        void WaitFor(string token, int ms)
        {
            string? l;
            while ((l = ReadLineTimeout(ms)) != null)
                if (l.StartsWith(token)) return;
            throw new Exception("Stockfish закрылся");
        }

        public string? BestMove(string fen, int movetime = 1200)
        {
            lock (this)
            {
                try
                {
                    if (_p == null || _p.HasExited) Launch();
                    Send("position fen " + fen);
                    Send("go movetime " + movetime);
                    string? l;
                    while ((l = ReadLineTimeout(movetime + 8000)) != null)
                    {
                        if (l.StartsWith("bestmove"))
                        {
                            var a = l.Split(' ');
                            return a.Length > 1 ? a[1] : null;
                        }
                    }
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
            try { if (_p != null) Send("quit"); } catch { }
            Kill();
        }
    }

    // ======================= РАСПОЗНАВАНИЕ ДОСКИ =======================
    class BoardRecognizer
    {
        public int X, Y, Size;
        public bool WhiteBottom = true;

        const int N = 24;
        const int MinPiece = 25;
        const string StartPos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";

        class Feat
        {
            public byte[] G = new byte[N * N];
            public bool[] M = new bool[N * N];
            public int Cnt;
        }

        readonly Dictionary<char, List<Feat>> _tpl = new();
        public bool Calibrated => _tpl.Count > 0;

        Feat Analyze(int[] px)
        {
            var f = new Feat();
            int[][] pts = { new[] { 1, 12 }, new[] { 22, 12 }, new[] { 21, 2 }, new[] { 2, 21 } };
            var rs = new int[4]; var gs = new int[4]; var bs = new int[4];
            for (int k = 0; k < 4; k++)
            {
                int p = px[pts[k][1] * N + pts[k][0]];
                rs[k] = (p >> 16) & 255; gs[k] = (p >> 8) & 255; bs[k] = p & 255;
            }
            Array.Sort(rs); Array.Sort(gs); Array.Sort(bs);
            int br = (rs[1] + rs[2]) / 2, bg = (gs[1] + gs[2]) / 2, bb = (bs[1] + bs[2]) / 2;

            for (int y = 3; y < 21; y++)
                for (int x = 3; x < 21; x++)
                {
                    int i = y * N + x;
                    int p = px[i];
                    int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                    f.G[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    if (Math.Abs(r - br) + Math.Abs(g - bg) + Math.Abs(b - bb) > 70)
                    {
                        f.M[i] = true;
                        f.Cnt++;
                    }
                }
            return f;
        }

        Feat[,] Extract(Bitmap board)
        {
            int s = Math.Min(board.Width, board.Height);
            int cs = s / 8;
            var res = new Feat[8, 8];
            var px = new int[N * N];
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    using var cell = Bitmap.CreateBitmap(board, c * cs, r * cs, cs, cs)!;
                    using var sm = Bitmap.CreateScaledBitmap(cell, N, N, true)!;
                    sm.GetPixels(px, 0, N, 0, 0, N, N);
                    res[r, c] = Analyze(px);
                }
            return res;
        }

        static long Distance(Feat a, Feat b)
        {
            long d = 0;
            for (int i = 0; i < N * N; i++)
            {
                if (!a.M[i] && !b.M[i]) continue;
                if (a.M[i] != b.M[i]) d += 255;
                else d += Math.Abs(a.G[i] - b.G[i]);
            }
            return d;
        }

        static char LogicalPiece(int r, int c)
        {
            return r switch
            {
                0 => "rnbqkbnr"[c],
                1 => 'p',
                6 => 'P',
                7 => "RNBQKBNR"[c],
                _ => '.'
            };
        }

        // Калибровка по стартовой позиции. board = картинка только доски.
        public bool Calibrate(Bitmap board, out string err)
        {
            err = "";
            var f = Extract(board);

            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    bool has = f[r, c].Cnt >= MinPiece;
                    bool edge = r < 2 || r > 5;
                    if (edge && !has)
                    {
                        err = "не вижу всех фигур (проверьте X, Y и размер доски)";
                        return false;
                    }
                    if (!edge && has)
                    {
                        err = "в центре доски есть фигуры";
                        return false;
                    }
                }

            double top = 0, bottom = 0; long tc = 0, bc = 0;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if (r > 1 && r < 6) continue;
                    var ft = f[r, c];
                    for (int i = 0; i < N * N; i++)
                        if (ft.M[i])
                        {
                            if (r < 2) { top += ft.G[i]; tc++; }
                            else { bottom += ft.G[i]; bc++; }
                        }
                }
            double topAvg = tc > 0 ? top / tc : 0, botAvg = bc > 0 ? bottom / bc : 0;
            WhiteBottom = botAvg > topAvg;

            _tpl.Clear();
            for (int sr = 0; sr < 8; sr++)
                for (int sc = 0; sc < 8; sc++)
                {
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    char ch = LogicalPiece(lr, lc);
                    if (ch == '.') continue;
                    if (!_tpl.ContainsKey(ch)) _tpl[ch] = new List<Feat>();
                    _tpl[ch].Add(f[sr, sc]);
                }
            return true;
        }

        // Логическая доска: [0,*] = 8-я горизонталь, [*,0] = вертикаль a
        public char[,] Recognize(Bitmap board)
        {
            var f = Extract(board);
            var res = new char[8, 8];
            for (int sr = 0; sr < 8; sr++)
                for (int sc = 0; sc < 8; sc++)
                {
                    char best = '.';
                    if (f[sr, sc].Cnt >= MinPiece)
                    {
                        long bd = long.MaxValue;
                        foreach (var kv in _tpl)
                            foreach (var t in kv.Value)
                            {
                                long d = Distance(f[sr, sc], t);
                                if (d < bd) { bd = d; best = kv.Key; }
                            }
                    }
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    res[lr, lc] = best;
                }
            return res;
        }

        public static string? ToFen(char[,] b, char side)
        {
            int wk = 0, bk = 0;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if (b[r, c] == 'K') wk++;
                    if (b[r, c] == 'k') bk++;
                }
            if (wk != 1 || bk != 1) return null;

            var sb = new StringBuilder();
            for (int r = 0; r < 8; r++)
            {
                int e = 0;
                for (int c = 0; c < 8; c++)
                {
                    if (b[r, c] == '.') e++;
                    else
                    {
                        if (e > 0) { sb.Append(e); e = 0; }
                        sb.Append(b[r, c]);
                    }
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
            sb.Append(" " + side + " " + castle + " - 0 1");
            return sb.ToString();
        }

        // "e2" -> центр клетки в координатах экрана
        public (float x, float y) Center(string sq)
        {
            int file = sq[0] - 'a', rank = sq[1] - '1';
            int col = WhiteBottom ? file : 7 - file;
            int row = WhiteBottom ? 7 - rank : rank;
            float cs = Size / 8f;
            return (X + col * cs + cs / 2, Y + row * cs + cs / 2);
        }
    }

    // ======================= СТРЕЛКА ПОВЕРХ ЭКРАНА =======================
    class ArrowView : Android.Views.View
    {
        float x1, y1, x2, y2;
        bool has;
        readonly Paint p = new Paint { Color = Color.Red, StrokeWidth = 14, AntiAlias = true };

        public ArrowView(Context c) : base(c)
        {
            p.SetStyle(Paint.Style.FillAndStroke);
        }

        public void Set(float a, float b, float c, float d)
        {
            x1 = a; y1 = b; x2 = c; y2 = d; has = true;
            Invalidate();
        }

        public void Clear()
        {
            has = false;
            Invalidate();
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (!has || canvas == null) return;
            var loc = new int[2];
            GetLocationOnScreen(loc);
            canvas.Translate(-loc[0], -loc[1]);
            canvas.DrawLine(x1, y1, x2, y2, p);
            double ang = Math.Atan2(y2 - y1, x2 - x1);
            float len = 50;
            var path = new Android.Graphics.Path();
            path.MoveTo(x2, y2);
            path.LineTo(x2 - len * (float)Math.Cos(ang - 0.5), y2 - len * (float)Math.Sin(ang - 0.5));
            path.LineTo(x2 - len * (float)Math.Cos(ang + 0.5), y2 - len * (float)Math.Sin(ang + 0.5));
            path.Close();
            canvas.DrawPath(path, p);
        }
    }

    class ProjCallback : MediaProjection.Callback { }

    // ======================= ГЛАВНЫЙ ЭКРАН =======================
    [Activity(Label = "ChessHelper", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int ReqProjection = 100;
        EditText? _x, _y, _size;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            AppLog.Hook();

            var m = Resources!.DisplayMetrics!;
            var prefs = GetSharedPreferences("cfg", FileCreationMode.Private)!;

            var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            layout.SetPadding(40, 60, 40, 40);

            layout.AddView(new TextView(this)
            {
                Text = "Область доски на экране (в пикселях).\nЭкран: " + m.WidthPixels + " x " + m.HeightPixels
            });

            _x = Field(layout, "Левый край X", prefs.GetInt("x", 0).ToString());
            _y = Field(layout, "Верхний край Y", prefs.GetInt("y", m.WidthPixels * 657 / 1000).ToString());
            _size = Field(layout, "Размер доски (ширина)", prefs.GetInt("s", m.WidthPixels).ToString());

            var btn = new Android.Widget.Button(this) { Text = "Запустить помощник" };
            btn.Click += (s, e) => StartHelper();
            layout.AddView(btn);

            var log = AppLog.Read();
            if (log.Length > 0)
            {
                layout.AddView(new TextView(this) { Text = "\nПоследние записи журнала:\n" + log });
                var clr = new Android.Widget.Button(this) { Text = "Очистить журнал" };
                clr.Click += (s, e) => { AppLog.Clear(); Toast.MakeText(this, "Журнал очищен", ToastLength.Short)!.Show(); };
                layout.AddView(clr);
            }

            var scroll = new ScrollView(this);
            scroll.AddView(layout);
            SetContentView(scroll);

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
                Toast.MakeText(this, "Разрешите показ поверх других окон и нажмите кнопку снова", ToastLength.Long)!.Show();
                return;
            }
            var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqProjection);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqProjection || resultCode != Result.Ok || data == null) return;

            int bx = int.TryParse(_x!.Text, out var vx) ? vx : 0;
            int by = int.TryParse(_y!.Text, out var vy) ? vy : 0;
            int bs = int.TryParse(_size!.Text, out var vs) ? vs : 800;

            GetSharedPreferences("cfg", FileCreationMode.Private)!.Edit()!
                .PutInt("x", bx)!.PutInt("y", by)!.PutInt("s", bs)!.Apply();

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

    // ======================= СЕРВИС: ЗАХВАТ, ПАНЕЛЬ, АНАЛИЗ =======================
    [Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
    public class CaptureService : Service
    {
        MediaProjection? _proj;
        ImageReader? _reader;
        VirtualDisplay? _vd;
        int _w, _h;
        IWindowManager? _wm;
        ArrowView? _arrow;
        LinearLayout? _panel;
        Android.Widget.Button? _bAuto, _bSide, _bFlip;

        readonly BoardRecognizer _rec = new();
        readonly StockfishEngine _engine = new();
        readonly Handler _main = new Handler(Looper.MainLooper!);
        readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        readonly object _cacheLock = new();

        char _side = 'w';
        char _mine = 'w';
        volatile bool _auto = true;
        bool _arrowVisible;
        bool _warned;
        byte[]? _baseline;
        volatile bool _needBase;
        Bitmap? _cache;
        CancellationTokenSource? _cts;

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            AppLog.Hook();

            var ch = new NotificationChannel("chess", "ChessHelper", NotificationImportance.Low);
            ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(ch);
            var n = new Notification.Builder(this, "chess")
                .SetContentTitle("ChessHelper работает")
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo).Build();
            StartForeground(1, n, ForegroundService.TypeMediaProjection);

            if (_proj != null) return StartCommandResult.NotSticky;
            if (intent == null || !intent.HasExtra("data"))
            {
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            try
            {
                _rec.X = intent.GetIntExtra("bx", 0);
                _rec.Y = intent.GetIntExtra("by", 0);
                _rec.Size = Math.Max(64, intent.GetIntExtra("bs", 800) / 8 * 8);

                var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
                var data = (Intent)intent.GetParcelableExtra("data")!;
                _proj = mgr.GetMediaProjection(intent.GetIntExtra("code", 0), data);
                _proj!.RegisterCallback(new ProjCallback(), _main);

                var m = Resources!.DisplayMetrics!;
                _w = m.WidthPixels;
                _h = m.HeightPixels;
                _reader = ImageReader.NewInstance(_w, _h, (ImageFormatType)1, 3);
                _vd = _proj.CreateVirtualDisplay("cap", _w, _h, (int)m.DensityDpi,
                    (DisplayFlags)16, _reader!.Surface, null, null);

                var path = System.IO.Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libstockfish.so");
                Task.Run(() =>
                {
                    try { _engine.Init(path); ShowToast("Stockfish готов"); }
                    catch (Exception ex) { AppLog.Write("Stockfish: " + ex); ShowToast("Ошибка Stockfish: " + ex.Message); }
                });

                ShowOverlay(_rec.Y + _rec.Size + 10);

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                Task.Run(() => AutoLoop(ct));
                ShowToast("Откройте шахматы со стартовой позицией");
            }
            catch (Exception ex)
            {
                AppLog.Write("Start: " + ex);
                ShowToast("Ошибка запуска: " + ex.Message);
                StopSelf();
            }
            return StartCommandResult.NotSticky;
        }

        void ShowToast(string s)
        {
            _main.Post(() => Android.Widget.Toast.MakeText(this, s, ToastLength.Long)!.Show());
        }

        Task RunMain(Action a)
        {
            var tcs = new TaskCompletionSource();
            _main.Post(() =>
            {
                try { a(); }
                catch (Exception e) { AppLog.Write("UI: " + e); }
                tcs.SetResult();
            });
            return tcs.Task;
        }

        async Task Safe(Func<Task> f)
        {
            try { await f(); }
            catch (Exception ex)
            {
                AppLog.Write(ex.ToString());
                ShowToast("Ошибка: " + ex.Message);
            }
        }

        // ---------- панель кнопок под доской ----------
        Android.Widget.Button MkBtn(string t)
        {
            var b = new Android.Widget.Button(this) { Text = t };
            b.TextSize = 11;
            b.SetAllCaps(false);
            b.SetPadding(14, 4, 14, 4);
            b.SetMinWidth(0);
            b.SetMinimumWidth(0);
            b.SetMinHeight(0);
            b.SetMinimumHeight(0);
            return b;
        }

        void ShowOverlay(int panelY)
        {
            _wm = GetSystemService(WindowService).JavaCast<IWindowManager>();

            _arrow = new ArrowView(this);
            var lpA = new WindowManagerLayoutParams(-1, -1,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable |
                WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits,
                Format.Translucent);
            _wm!.AddView(_arrow, lpA);

            _panel = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            _panel.SetBackgroundColor(Color.Argb(160, 0, 0, 0));

            _bAuto = MkBtn("Авто: вкл");
            var bAn = MkBtn("Анализ");
            var bCal = MkBtn("Калибр.");
            _bSide = MkBtn("Ход: белых");
            _bFlip = MkBtn("Белые снизу");
            var bExit = MkBtn("Выход");

            _bAuto.Click += (s, e) =>
            {
                _auto = !_auto;
                UpdateButtons();
            };
            bAn.Click += async (s, e) => await Safe(async () =>
            {
                await _busy.WaitAsync();
                try { await AnalyzeCore(true); } finally { _busy.Release(); }
            });
            bCal.Click += async (s, e) => await Safe(async () =>
            {
                await _busy.WaitAsync();
                try { await CalibrateManual(); } finally { _busy.Release(); }
            });
            _bSide.Click += (s, e) =>
            {
                _side = _side == 'w' ? 'b' : 'w';
                UpdateButtons();
            };
            _bFlip.Click += (s, e) =>
            {
                _rec.WhiteBottom = !_rec.WhiteBottom;
                _mine = _rec.WhiteBottom ? 'w' : 'b';
                UpdateButtons();
            };
            bExit.Click += (s, e) => StopSelf();

            foreach (var b in new[] { _bAuto, bAn, bCal, _bSide, _bFlip, bExit }) _panel.AddView(b);

            var lp = new WindowManagerLayoutParams(-2, -2,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable, Format.Translucent)
            { Gravity = GravityFlags.Top | GravityFlags.Left, X = 0, Y = panelY };
            _wm.AddView(_panel, lp);
        }

        void UpdateButtons()
        {
            _main.Post(() =>
            {
                if (_bAuto != null) _bAuto.Text = _auto ? "Авто: вкл" : "Авто: выкл";
                if (_bSide != null) _bSide.Text = _side == 'w' ? "Ход: белых" : "Ход: чёрных";
                if (_bFlip != null) _bFlip.Text = _rec.WhiteBottom ? "Белые снизу" : "Чёрные снизу";
            });
        }

        // ---------- захват экрана ----------
        Bitmap? Crop(Android.Media.Image img)
        {
            var plane = img.GetPlanes()![0];
            int ps = plane.PixelStride, rs = plane.RowStride;
            int fw = rs / ps;
            using var full = Bitmap.CreateBitmap(fw, _h, Bitmap.Config.Argb8888!)!;
            full.CopyPixelsFromBuffer(plane.Buffer!);

            int x = Math.Clamp(_rec.X, 0, Math.Max(0, fw - 64));
            int y = Math.Clamp(_rec.Y, 0, Math.Max(0, _h - 64));
            int s = Math.Min(_rec.Size, Math.Min(_w - x, _h - y));
            s = s / 8 * 8;
            if (s < 64) return null;
            return Bitmap.CreateBitmap(full, x, y, s, s);
        }

        // Возвращает картинку доски. fresh = true, если пришёл новый кадр.
        async Task<(Bitmap? bmp, bool fresh)> Grab(int tries)
        {
            for (int i = 0; i < tries; i++)
            {
                Android.Media.Image? img = null;
                try
                {
                    img = _reader?.AcquireLatestImage();
                    if (img != null)
                    {
                        var bmp = Crop(img);
                        if (bmp != null)
                        {
                            lock (_cacheLock)
                            {
                                _cache?.Dispose();
                                _cache = bmp.Copy(Bitmap.Config.Argb8888!, false);
                            }
                            return (bmp, true);
                        }
                    }
                }
                catch (Java.Lang.IllegalStateException) { }
                finally
                {
                    try { img?.Close(); } catch { }
                    img?.Dispose();
                }
                if (i < tries - 1) await Task.Delay(100);
            }
            lock (_cacheLock)
            {
                if (_cache != null) return (_cache.Copy(Bitmap.Config.Argb8888!, false), false);
            }
            return (null, false);
        }

        static byte[] Small(Bitmap b)
        {
            using var sm = Bitmap.CreateScaledBitmap(b, 48, 48, true)!;
            var px = new int[48 * 48];
            sm.GetPixels(px, 0, 48, 0, 0, 48, 48);
            var g = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int p = px[i];
                g[i] = (byte)((((p >> 16) & 255) * 299 + ((p >> 8) & 255) * 587 + (p & 255) * 114) / 1000);
            }
            return g;
        }

        static double MeanDiff(byte[] a, byte[] b)
        {
            long s = 0;
            for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
            return (double)s / a.Length;
        }

        // ---------- калибровка ----------
        void OnCalibrated(Bitmap bmp)
        {
            _side = 'w';
            _mine = _rec.WhiteBottom ? 'w' : 'b';
            _baseline = Small(bmp);
            _needBase = false;
            _warned = false;
            UpdateButtons();
            ShowToast("Калибровка готова. Вы играете за " + (_mine == 'w' ? "белых" : "чёрных"));
        }

        async Task CalibrateManual()
        {
            await RunMain(() =>
            {
                _arrow!.Clear();
                _panel!.Visibility = ViewStates.Invisible;
            });
            _arrowVisible = false;
            try
            {
                await Task.Delay(350);
                var (bmp, _) = await Grab(8);
                using (bmp)
                {
                    if (bmp == null) { ShowToast("Нет изображения"); return; }
                    if (_rec.Calibrate(bmp, out var err)) OnCalibrated(bmp);
                    else ShowToast("Не стартовая позиция: " + err);
                }
            }
            finally
            {
                await RunMain(() => _panel!.Visibility = ViewStates.Visible);
            }
        }

        // ---------- анализ ----------
        async Task AnalyzeCore(bool hidePanel)
        {
            if (!_rec.Calibrated) { ShowToast("Сначала калибровка на стартовой позиции"); return; }

            bool wasArrow = _arrowVisible;
            await RunMain(() =>
            {
                _arrow!.Clear();
                if (hidePanel) _panel!.Visibility = ViewStates.Invisible;
            });
            _arrowVisible = false;

            string? fen = null;
            try
            {
                await Task.Delay(350);
                var (bmp, _) = await Grab((wasArrow || hidePanel) ? 8 : 1);
                using (bmp)
                {
                    if (bmp == null) { ShowToast("Нет изображения"); return; }
                    var grid = _rec.Recognize(bmp);
                    fen = BoardRecognizer.ToFen(grid, _side);
                }
            }
            finally
            {
                if (hidePanel) await RunMain(() => _panel!.Visibility = ViewStates.Visible);
            }

            if (fen == null)
            {
                ShowToast("Не вижу обоих королей. Проверьте область доски");
                _needBase = true;
                _baseline = null;
                return;
            }

            string? move = await Task.Run(() => _engine.BestMove(fen));
            if (move == null || move.Length < 4 || move == "(none)")
            {
                ShowToast("Ходов нет");
                _needBase = true;
                _baseline = null;
                return;
            }

            var a = _rec.Center(move.Substring(0, 2));
            var b = _rec.Center(move.Substring(2, 2));
            await RunMain(() => _arrow!.Set(a.x, a.y, b.x, b.y));
            _arrowVisible = true;
            _needBase = true;
            _baseline = null;
            ShowToast("Лучший ход: " + move);
        }

        // ---------- автоматический режим ----------
        async Task AutoLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool got = false;
                try
                {
                    await Task.Delay(1000, ct);
                    if (!_auto) continue;
                    await _busy.WaitAsync(ct);
                    got = true;
                    await Tick(ct);
                }
                catch (System.OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Write(ex.ToString());
                    ShowToast("Ошибка: " + ex.Message);
                }
                finally
                {
                    if (got) _busy.Release();
                }
            }
        }

        async Task Tick(CancellationToken ct)
        {
            // 1. автокалибровка
            if (!_rec.Calibrated)
            {
                bool ok = false;
                string err = "";
                var (bmp0, _) = await Grab(1);
                using (bmp0)
                {
                    if (bmp0 == null) return;
                    if (_rec.Calibrate(bmp0, out err))
                    {
                        OnCalibrated(bmp0);
                        ok = true;
                    }
                }
                if (ok)
                {
                    if (_side == _mine) await AnalyzeCore(false);
                }
                else if (!_warned)
                {
                    _warned = true;
                    ShowToast("Жду стартовую позицию: " + err);
                }
                return;
            }

            // 2. слежение за доской
            byte[]? small = null;
            {
                var (cur, fresh) = await Grab(1);
                using (cur)
                {
                    if (cur == null || !fresh) return;
                    small = Small(cur);
                }
            }

            if (_needBase || _baseline == null)
            {
                _baseline = small;
                _needBase = false;
                return;
            }
            if (MeanDiff(small, _baseline) < 3.0) return;

            // доска изменилась: ждём, пока закончится анимация
            byte[] last = small;
            int stable = 0;
            for (int i = 0; i < 14 && stable < 2; i++)
            {
                await Task.Delay(400, ct);
                var (b2, f2) = await Grab(1);
                using (b2)
                {
                    if (b2 == null || !f2) { stable++; continue; }
                    var s2 = Small(b2);
                    if (MeanDiff(s2, last) < 1.5) stable++; else stable = 0;
                    last = s2;
                }
            }

            _side = _side == 'w' ? 'b' : 'w';
            UpdateButtons();

            if (_side == _mine)
            {
                await AnalyzeCore(false);
            }
            else
            {
                await RunMain(() => _arrow!.Clear());
                _arrowVisible = false;
                _needBase = true;
                _baseline = null;
            }
        }

        public override void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            try { if (_arrow != null) _wm?.RemoveView(_arrow); } catch { }
            try { if (_panel != null) _wm?.RemoveView(_panel); } catch { }
            try { _engine.Dispose(); } catch { }
            try { _vd?.Release(); } catch { }
            try { _proj?.Stop(); } catch { }
            lock (_cacheLock) { _cache?.Dispose(); _cache = null; }
            base.OnDestroy();
        }
    }
}
