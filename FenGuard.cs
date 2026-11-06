using Android.Content;
using Android.OS;
using Android.Widget;

namespace ChessHelper
{
    public static class FenGuard
    {
        // Возвращает текст ошибки или null, если позиция выглядит возможной.
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

            // Сторона, которая НЕ ходит, не может стоять под шахом
            bool whiteMoves = side == 'w';
            char enemyKing = whiteMoves ? 'k' : 'K';
            if (IsAttacked(b, enemyKing, whiteMoves))
                return "Король стороны, не имеющей хода, под шахом";
            return null;
        }

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

            // Пешки: белые бьют вверх (к меньшим r), чёрные вниз
            int pr = byWhite ? kr + 1 : kr - 1;
            foreach (int dc in new[] { -1, 1 })
                if (In(pr, kc + dc) && IsType(b[pr, kc + dc], 'p', byWhite)) return true;

            // Кони
            int[] kn = { -2, -1, -2, 1, -1, -2, -1, 2, 1, -2, 1, 2, 2, -1, 2, 1 };
            for (int i = 0; i < kn.Length; i += 2)
            {
                int r = kr + kn[i], c = kc + kn[i + 1];
                if (In(r, c) && IsType(b[r, c], 'n', byWhite)) return true;
            }

            // Король рядом
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int r = kr + dr, c = kc + dc;
                    if (In(r, c) && IsType(b[r, c], 'k', byWhite)) return true;
                }

            // Дальнобойные фигуры
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

        // Показывает текст на экране и копирует его в буфер обмена
        public static void Show(string text)
        {
            new Handler(Looper.MainLooper!).Post(() =>
            {
                var ctx = Android.App.Application.Context;
                try
                {
                    var cm = (ClipboardManager?)ctx.GetSystemService(Context.ClipboardService);
                    if (cm != null) cm.PrimaryClip = ClipData.NewPlainText("fen", text);
                }
                catch { }
                Toast.MakeText(ctx, text, ToastLength.Long)!.Show();
            });
        }
    }
}
