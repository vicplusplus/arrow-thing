using System;
using System.Collections.Generic;

public static class ListUtils
{
    public static List<T> Shuffle<T>(List<T> list, Random random)
    {
        // Fisher-Yates
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (list[n], list[k]) = (list[k], list[n]);
        }

        return list;
    }
}
