using Android.OS;
using Android.Widget;

namespace ChessHelper
{
    public static class FenGuard
    {
        // ---------- Проверка, что позиция вообще возможна ----------
        // b[0,*] = 8-я горизонталь, b[*,0] = вертикаль a. Заглавные = белые.
        public static string? Validate(char[,] b, char side)
        {
            int wk = 0, bk = 0;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    char ch = b[r, c];
                    if (ch == 'K') wk++;
                    if (ch == 'k') bk++;
                    if ((ch == 'P' || ch == 'p') && (r == 0 || r == 7))
                        return "Пешка на крайней горизонтали";
                }
            if (wk != 1 || bk != 1)
                return "Королей: белых " + wk + ", чёрных " + bk;

            bool whiteMoves = side == 'w';
            char enemyKing = whiteMoves ? 'k' : 'K';
            if (IsAttacked(b, enemyKing, whiteMoves))
                return "Король стороны, не имеющей хода, под шахом";
            return null;
        }

        // ---------- Проверка преемственности позиций ----------
        // Сравнивает количество фигур с прошлой принятой позицией.
        // Фигуры не могут появиться из ничего, а за один-два хода
        // не может пропасть больше трёх штук.
        const string Kinds = "PNBRQpnbrq";
        static int[]? _base;
        static string? _rejected;

        static int[] Count(char[,] b)
        {
            var c = new int[10];
            for (int r = 0; r < 8; r++)
                for (int col = 0; col < 8; col++)
                {
                    int i = Kinds.IndexOf(b[r, col]);
                    if (i >= 0) c[i]++;
                }
            return c;
        }

        public static void Reset()
        {
            _base = null;
            _rejected = null;
        }

        // Возвращает текст предупреждения или null, если всё в порядке.
        // Если нажать «Анализ» ещё раз на той же позиции, она будет принята.
        public static string? CheckContinuity(char[,] b)
        {
            var cur = Count(b);
            int sumCur = cur.Sum();

            // Почти полный набор фигур: это начало партии, начинаем заново
            if (_base == null || sumCur >= 30)
            {
                _base = cur;
                _rejected = null;
                return null;
            }

            int sumBase = _base.Sum();
            string? problem = null;

            if (sumCur > sumBase)
            {
                problem = "фигур на доске стало больше, чем было";
            }
            else if (sumBase - sumCur > 3)
            {
                problem = "пропало слишком много фигур (" + (sumBase - sumCur) + ")";
            }
            else
            {
                for (int side = 0; side < 2 && problem == null; side++)
                {
                    int o = side * 5; // 0 = белые, 5 = чёрные
                    int pawnLoss = Math.Max(0, _base[o] - cur[o]);
                    int appeared = 0;
                    for (int k = 1; k < 5; k++)
                        appeared += Math.Max(0, cur[o + k] - _base[o + k]);
                    if (appeared > pawnLoss)
                        problem = (side == 0 ? "у белых" : "у чёрных") +
                                  " появилась лишняя фигура (возможно, ферзь принят за коня или наоборот)";
                }
            }

            if (problem == null)
            {
                _base = cur;
                _rejected = null;
                return null;
            }

            string key = string.Join(",", cur);
            if (_rejected == key)
            {
                // Тот же результат второй раз подряд: считаем, что распознано верно
                _base = cur;
                _rejected = null;
                return null;
            }

            _rejected = key;
            return "Сомнительная позиция: " + problem +
                   ". Проверьте доску. Нажмите «Анализ» ещё раз, чтобы принять.";
        }

        // ---------- Шахи ----------
        static bool IsType(char ch, char type, bool white) =>
            white ? ch == char.ToUpper(type) : ch == char.ToLower(type);

        static bool IsAttacked(char[,] b, char king, bool byWhite)
        {
            int kr = -1, kc = -1;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (b[r, c] == king) { kr = r; kc = c; }
            if (kr < 0) return false;

            bool In(int r, int c) => r >= 0 && r < 8 && c >= 0 && c < 8;

            int pr = byWhite ? kr + 1 : kr - 1;
            foreach (int dc in new[] { -1, 1 })
                if (In(pr, kc + dc) && IsType(b[pr, kc + dc], 'p', byWhite)) return true;

            int[] kn = { -2, -1, -2, 1, -1, -2, -1, 2, 1, -2, 1, 2, 2, -1, 2, 1 };
            for (int i = 0; i < kn.Length; i += 2)
            {
                int r = kr + kn[i], c = kc + kn[i + 1];
                if (In(r, c) && IsType(b[r, c], 'n', byWhite)) return true;
            }

            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int r = kr + dr, c = kc + dc;
                    if (In(r, c) && IsType(b[r, c], 'k', byWhite)) return true;
                }

            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    bool diagonal = dr != 0 && dc != 0;
                    int r = kr + dr, c = kc + dc;
                    while (In(r, c))
                    {
                        char ch = b[r, c];
                        if (ch != '.')
                        {
                            if (IsType(ch, 'q', byWhite)) return true;
                            if (diagonal && IsType(ch, 'b', byWhite)) return true;
                            if (!diagonal && IsType(ch, 'r', byWhite)) return true;
                            break;
                        }
                        r += dr; c += dc;
                    }
                }
            return false;
        }

        // ---------- Сообщение на экране (без буфера обмена) ----------
        public static void Show(string text)
        {
            new Handler(Looper.MainLooper!).Post(() =>
            {
                Toast.MakeText(Android.App.Application.Context, text, ToastLength.Long)!.Show();
            });
        }
    }
}