using System.Diagnostics;
using System.Text;
using Android.Graphics;

namespace ChessHelper
{
    // ---------- Stockfish по протоколу UCI ----------
    public class StockfishEngine : IDisposable
    {
        Process? _p;

        public void Start(string path)
        {
            _p = new Process();
            _p.StartInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _p.Start();
            Send("uci");
            WaitFor("uciok");
            Send("isready");
            WaitFor("readyok");
        }

        void Send(string s)
        {
            _p!.StandardInput.WriteLine(s);
            _p.StandardInput.Flush();
        }

        void WaitFor(string token)
        {
            string? line;
            while ((line = _p!.StandardOutput.ReadLine()) != null)
                if (line.StartsWith(token)) return;
        }

        public string? BestMove(string fen, int ms = 1500)
        {
            Send("position fen " + fen);
            Send("go movetime " + ms);
            string? line;
            while ((line = _p!.StandardOutput.ReadLine()) != null)
                if (line.StartsWith("bestmove"))
                    return line.Split(' ')[1];
            return null;
        }

        public void Dispose()
        {
            try { Send("quit"); _p?.Kill(); } catch { }
        }
    }

    // ---------- Распознавание доски по шаблонам ----------
    public class BoardRecognizer
    {
        public int X, Y, Size;
        public bool WhiteBottom = true;
        const int N = 32;
        readonly Dictionary<char, List<int[]>> _tpl = new Dictionary<char, List<int[]>>();

        public bool Calibrated => _tpl.Count > 0;

        int[] Cell(Bitmap s, int sr, int sc)
        {
            int cs = Size / 8;
            using var c = Bitmap.CreateBitmap(s, X + sc * cs, Y + sr * cs, cs, cs);
            using var sm = Bitmap.CreateScaledBitmap(c, N, N, true)!;
            var a = new int[N * N];
            sm.GetPixels(a, 0, N, 0, 0, N, N);
            return a;
        }

        // Вызывать, когда на доске стартовая позиция
        public void Calibrate(Bitmap screen)
        {
            _tpl.Clear();
            for (int sr = 0; sr < 8; sr++)
                for (int sc = 0; sc < 8; sc++)
                {
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    char ch = LogicalPiece(lr, lc);
                    if (!_tpl.ContainsKey(ch)) _tpl[ch] = new List<int[]>();
                    _tpl[ch].Add(Cell(screen, sr, sc));
                }
        }

        static char LogicalPiece(int r, int c)
        {
            switch (r)
            {
                case 0: return "rnbqkbnr"[c];
                case 1: return 'p';
                case 6: return 'P';
                case 7: return "RNBQKBNR"[c];
                default: return '.';
            }
        }

        // [0,*] = 8-я горизонталь, [*,0] = вертикаль a
        public char[,] Recognize(Bitmap screen)
        {
            var b = new char[8, 8];
            for (int sr = 0; sr < 8; sr++)
                for (int sc = 0; sc < 8; sc++)
                {
                    var cell = Cell(screen, sr, sc);
                    char best = '.';
                    long bestD = long.MaxValue;
                    foreach (var kv in _tpl)
                        foreach (var t in kv.Value)
                        {
                            long d = Diff(cell, t);
                            if (d < bestD) { bestD = d; best = kv.Key; }
                        }
                    int lr = WhiteBottom ? sr : 7 - sr;
                    int lc = WhiteBottom ? sc : 7 - sc;
                    b[lr, lc] = best;
                }
            return b;
        }

        static long Diff(int[] a, int[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                sum += Math.Abs(((a[i] >> 16) & 255) - ((b[i] >> 16) & 255));
                sum += Math.Abs(((a[i] >> 8) & 255) - ((b[i] >> 8) & 255));
                sum += Math.Abs((a[i] & 255) - (b[i] & 255));
            }
            return sum;
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

        // "e2" -> центр клетки на экране
        public (float x, float y) Center(string sq)
        {
            int file = sq[0] - 'a', rank = sq[1] - '1';
            int col = WhiteBottom ? file : 7 - file;
            int row = WhiteBottom ? 7 - rank : rank;
            float cs = Size / 8f;
            return (X + col * cs + cs / 2, Y + row * cs + cs / 2);
        }
    }
}
