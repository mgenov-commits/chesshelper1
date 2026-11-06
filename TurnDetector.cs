using Android.Graphics;

namespace ChessHelper
{
    public static class TurnDetector
    {
        // Возвращает 'w' или 'b' (чей ход) либо null, если определить нельзя
        public static char? Detect(Bitmap bmp, char[,] grid, int bx, int by, int size, bool whiteBottom)
        {
            try
            {
                bool cropped = bmp.Width <= size + 4 && bmp.Height <= size + 4;
                int ox = cropped ? 0 : bx, oy = cropped ? 0 : by;
                float cs = size / 8f;
                int inset = Math.Max(3, (int)(cs * 0.08f));

                // Цвет фона клетки: среднее двух углов без подписей (верх-право и низ-лево)
                var rgb = new int[8, 8, 3];
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                    {
                        int x1 = Clamp(ox + (int)((c + 1) * cs) - inset, bmp.Width);
                        int y1 = Clamp(oy + (int)(r * cs) + inset, bmp.Height);
                        int x2 = Clamp(ox + (int)(c * cs) + inset, bmp.Width);
                        int y2 = Clamp(oy + (int)((r + 1) * cs) - inset, bmp.Height);
                        int p1 = bmp.GetPixel(x1, y1), p2 = bmp.GetPixel(x2, y2);
                        rgb[r, c, 0] = (((p1 >> 16) & 255) + ((p2 >> 16) & 255)) / 2;
                        rgb[r, c, 1] = (((p1 >> 8) & 255) + ((p2 >> 8) & 255)) / 2;
                        rgb[r, c, 2] = ((p1 & 255) + (p2 & 255)) / 2;
                    }

                // Обычный цвет светлых и тёмных клеток = медиана по каждой чётности
                var med = new int[2, 3];
                for (int par = 0; par < 2; par++)
                    for (int ch = 0; ch < 3; ch++)
                    {
                        var list = new List<int>();
                        for (int r = 0; r < 8; r++)
                            for (int c = 0; c < 8; c++)
                                if ((r + c) % 2 == par) list.Add(rgb[r, c, ch]);
                        list.Sort();
                        med[par, ch] = list[list.Count / 2];
                    }

                int lit = 0, withPiece = 0, emptyCells = 0;
                char piece = '.';
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                    {
                        int par = (r + c) % 2;
                        int d = Math.Abs(rgb[r, c, 0] - med[par, 0])
                              + Math.Abs(rgb[r, c, 1] - med[par, 1])
                              + Math.Abs(rgb[r, c, 2] - med[par, 2]);
                        if (d < 70) continue;
                        lit++;
                        int lr = whiteBottom ? r : 7 - r;
                        int lc = whiteBottom ? c : 7 - c;
                        char p = grid[lr, lc];
                        if (p == '.') emptyCells++;
                        else { withPiece++; piece = p; }
                    }

                // Нужен ровно один ход: две подсвеченные клетки, на одной стоит фигура
                if (lit != 2 || withPiece != 1 || emptyCells != 1) return null;
                return char.IsUpper(piece) ? 'b' : 'w';
            }
            catch { return null; }
        }

        static int Clamp(int v, int max) => Math.Max(0, Math.Min(max - 1, v));
    }
}
