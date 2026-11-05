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
using Android.Util;
using Android.Views;
using Android.Widget;

namespace ChessHelper
{
    // ---------- Главный экран ----------
    [Activity(Label = "ChessHelper", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int ReqProjection = 100;
        EditText? _x, _y, _size;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var m = Resources!.DisplayMetrics!;
            var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            layout.SetPadding(40, 60, 40, 40);

            layout.AddView(new TextView(this)
            {
                Text = "Область доски на экране (в пикселях).\nЭкран: " + m.WidthPixels + " x " + m.HeightPixels
            });

            _x = Field(layout, "Левый край X", "0");
            _y = Field(layout, "Верхний край Y", ((m.HeightPixels - m.WidthPixels) / 2).ToString());
            _size = Field(layout, "Размер доски (ширина)", m.WidthPixels.ToString());

            var btn = new Button(this) { Text = "Запустить помощник" };
            btn.Click += (s, e) => StartHelper();
            layout.AddView(btn);

            SetContentView(layout);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 1);
        }

        EditText Field(LinearLayout parent, string hint, string value)
        {
            var e = new EditText(this) { Hint = hint, Text = value };
            e.InputType = Android.Text.InputTypes.ClassNumber;
            parent.AddView(e);
            return e;
        }

        void StartHelper()
        {
            if (!Settings.CanDrawOverlays(this))
            {
                StartActivity(new Intent(Settings.ActionManageOverlayPermission,
                    Android.Net.Uri.Parse("package:" + PackageName)));
                Android.Widget.Toast.MakeText(this,
                    "Разрешите показ поверх других окон и нажмите кнопку снова",
                    ToastLength.Long)!.Show();
                return;
            }
            var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqProjection);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqProjection || resultCode != Result.Ok || data == null) return;

            var i = new Intent(this, typeof(CaptureService));
            i.PutExtra("code", (int)resultCode);
            i.PutExtra("data", data);
            i.PutExtra("bx", int.Parse(_x!.Text!));
            i.PutExtra("by", int.Parse(_y!.Text!));
            i.PutExtra("bs", int.Parse(_size!.Text!));
            StartForegroundService(i);
            MoveTaskToBack(true);
        }
    }

    // ---------- Стрелка поверх экрана ----------
    class ArrowView : View
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

    class ProjCallback : MediaProjection.Callback { }

    // ---------- Сервис: захват экрана, кнопки, Stockfish ----------
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
        Bitmap? _last;
        readonly BoardRecognizer _rec = new BoardRecognizer();
        readonly StockfishEngine _engine = new StockfishEngine();
        char _side = 'w';

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            var ch = new NotificationChannel("chess", "ChessHelper", NotificationImportance.Low);
            ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(ch);
            var n = new Notification.Builder(this, "chess")
                .SetContentTitle("ChessHelper работает")
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!
                .Build()!;
            StartForeground(1, n, ForegroundService.TypeMediaProjection);

            _rec.X = intent!.GetIntExtra("bx", 0);
            _rec.Y = intent.GetIntExtra("by", 0);
            _rec.Size = intent.GetIntExtra("bs", 800);

            var mgr = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            var data = (Intent)intent.GetParcelableExtra("data")!;
            _proj = mgr.GetMediaProjection(intent.GetIntExtra("code", 0), data);
            _proj!.RegisterCallback(new ProjCallback(), new Handler(Looper.MainLooper!));

            _wm = GetSystemService(WindowService).JavaCast<IWindowManager>();
            var metrics = new DisplayMetrics();
#pragma warning disable CA1422
            _wm!.DefaultDisplay!.GetRealMetrics(metrics);
#pragma warning restore CA1422
            _w = metrics.WidthPixels;
            _h = metrics.HeightPixels;

            _reader = ImageReader.NewInstance(_w, _h, (ImageFormatType)1, 2);
            _vd = _proj.CreateVirtualDisplay("cap", _w, _h, (int)metrics.DensityDpi,
                (DisplayFlags)16, _reader!.Surface, null, null);

            var path = System.IO.Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libstockfish.so");
            Task.Run(() =>
            {
                try { _engine.Start(path); ShowToast("Stockfish готов"); }
                catch (Exception ex) { ShowToast("Ошибка Stockfish: " + ex.Message); }
            });

            ShowOverlay();
            return StartCommandResult.Sticky;
        }

        void ShowToast(string s)
        {
            new Handler(Looper.MainLooper!).Post(() =>
                Android.Widget.Toast.MakeText(this, s, ToastLength.Long)!.Show());
        }

        void ShowOverlay()
        {
            // стрелка на весь экран, не перехватывает нажатия
            _arrow = new ArrowView(this);
            var lpA = new WindowManagerLayoutParams(-1, -1,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutInScreen,
                Format.Translucent);
            _wm!.AddView(_arrow, lpA);

            // панель кнопок
            _panel = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
            var bAn = new Button(this) { Text = "Анализ" };
            var bCal = new Button(this) { Text = "Калибровка" };
            var bSide = new Button(this) { Text = "Ход: белых" };
            var bFlip = new Button(this) { Text = "Белые снизу" };
            var bExit = new Button(this) { Text = "Выход" };

            bAn.Click += async (s, e) => await Analyze();
            bCal.Click += async (s, e) => await CalibrateAsync();
            bSide.Click += (s, e) =>
            {
                _side = _side == 'w' ? 'b' : 'w';
                bSide.Text = _side == 'w' ? "Ход: белых" : "Ход: чёрных";
            };
            bFlip.Click += (s, e) =>
            {
                _rec.WhiteBottom = !_rec.WhiteBottom;
                bFlip.Text = _rec.WhiteBottom ? "Белые снизу" : "Чёрные снизу";
            };
            bExit.Click += (s, e) => StopSelf();

            foreach (var b in new[] { bAn, bCal, bSide, bFlip, bExit }) _panel.AddView(b);

            var lp = new WindowManagerLayoutParams(-2, -2,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable, Format.Translucent)
            { Gravity = GravityFlags.Top | GravityFlags.Left, X = 10, Y = 150 };
            _wm.AddView(_panel, lp);
        }

        async Task<Bitmap?> Capture()
        {
            for (int i = 0; i < 15; i++)
            {
                using var img = _reader!.AcquireLatestImage();
                if (img != null)
                {
                    var plane = img.GetPlanes()![0];
                    int pad = plane.RowStride - plane.PixelStride * _w;
                    var full = Bitmap.CreateBitmap(_w + pad / plane.PixelStride, _h, Bitmap.Config.Argb8888!)!;
                    full.CopyPixelsFromBuffer(plane.Buffer);
                    _last = Bitmap.CreateBitmap(full, 0, 0, _w, _h);
                    return _last;
                }
                await Task.Delay(100);
            }
            return _last; // экран не менялся — берём последний кадр
        }

        async Task CalibrateAsync()
        {
            _arrow!.Clear();
            await Task.Delay(300);
            var bmp = await Capture();
            if (bmp == null) { ShowToast("Нет изображения"); return; }
            try { _rec.Calibrate(bmp); ShowToast("Калибровка готова"); }
            catch (Exception ex) { ShowToast("Ошибка: " + ex.Message); }
        }

        async Task Analyze()
        {
            if (!_rec.Calibrated) { ShowToast("Сначала калибровка на стартовой позиции"); return; }
            _arrow!.Clear();
            await Task.Delay(300);
            var bmp = await Capture();
            if (bmp == null) { ShowToast("Нет изображения"); return; }
            try
            {
                string? move = await Task.Run(() =>
                {
                    var grid = _rec.Recognize(bmp);
                    var fen = BoardRecognizer.ToFen(grid, _side);
                    return _engine.BestMove(fen);
                });
                if (move == null || move.Length < 4 || move == "(none)")
                {
                    ShowToast("Ходов нет");
                    return;
                }
                var a = _rec.Center(move.Substring(0, 2));
                var b = _rec.Center(move.Substring(2, 2));
                _arrow.Set(a.x, a.y, b.x, b.y);
                ShowToast("Лучший ход: " + move);
            }
            catch (Exception ex) { ShowToast("Ошибка: " + ex.Message); }
        }

        public override void OnDestroy()
        {
            try
            {
                if (_arrow != null) _wm?.RemoveView(_arrow);
                if (_panel != null) _wm?.RemoveView(_panel);
            }
            catch { }
            _engine.Dispose();
            _vd?.Release();
            _proj?.Stop();
            base.OnDestroy();
        }
    }
}
