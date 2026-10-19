// Układa listę postępu w drzewo: złożenie, pod nim (z wcięciem) jego podzłożenia i części.
//
// Kolejność WYŚWIETLANIA jest tu niezależna od kolejności PRACY. Wysyłka idzie od liści (złożenia
// nie da się podpiąć, zanim nie istnieją jego części), a pobieranie od góry — ale odhaczanie
// działa po kluczu, nie po miejscu na liście, więc lista może stać w kolejności drzewa, a ptaszki
// i tak trafią we właściwe pozycje.
//
// Liczone RAZ, tutaj, a nie w każdym makrze z osobna — ta sama zasada co przy liczeniu postępu:
// trzy CAD-y nie mają szans pokazać tego samego drzewa na trzy różne sposoby. Makra przysyłają
// tylko pary rodzic–dziecko, które i tak trzymają w pamięci do podpinania BOM.
static class ProgressTree
{
    private const int MaxRows = 2000;

    // Wynik to dwie rzeczy, bo plik i wiersz to nie to samo:
    //   Entries — każdy plik RAZ (w kolejności pierwszego wystąpienia w drzewie). Po nich idzie
    //             odhaczanie, licznik "x z y" i raport — bo każdy plik jest wysyłany raz.
    //   Rows    — wiersze do wyświetlenia. Część użyta w kilku złożeniach stoi pod KAŻDYM z nich
    //             (decyzja użytkownika: ma ją widzieć wszędzie, gdzie występuje), a jej status
    //             jest wspólny dla wszystkich wystąpień — wysłana raz, odhaczona wszędzie.
    public static (List<TransferProgressEntry> Entries, List<ProgressRow> Rows) Arrange(
        List<TransferProgressEntry> entries, IEnumerable<(string Parent, string Child)> edges)
    {
        var byKey = entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

        // Dzieci w kolejności, w jakiej przyszły relacje — u makr to kolejność z drzewa CAD,
        // przy pobieraniu kolejność pozycji w BOM.
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var isChild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (parent, child) in edges)
        {
            // Relacje do czegoś, czego nie ma na liście (albo element sam w sobie), nie mają gdzie
            // się podpiąć — pomijamy je zamiast wywracać całą listę.
            if (parent == child || !byKey.ContainsKey(parent) || !byKey.ContainsKey(child))
                continue;
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<string>();
            if (!list.Contains(child))
                list.Add(child);
            isChild.Add(child);
        }

        // Bez żadnej relacji (starsze makro, pojedynczy plik) lista zostaje dokładnie taka, jak
        // przyszła — płaska, jak dotąd.
        if (children.Count == 0)
            return (entries, entries.Select(e => new ProgressRow(e.Key, 0)).ToList());

        var rows = new List<ProgressRow>();
        var ordered = new List<TransferProgressEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Klucze na ścieżce od korzenia do bieżącego wiersza. Chroni przed cyklem w danych —
        // ten sam plik może stać w wielu gałęziach, ale nie może być własnym przodkiem.
        var ancestors = new HashSet<string>(StringComparer.Ordinal);

        void Place(string key, int depth)
        {
            // Podzłożenie użyte wiele razy rozwija się pod każdym wystąpieniem z całą
            // zawartością, więc przy dużym złożeniu wierszy przybywa szybko. Powyżej limitu
            // dokładamy już tylko pierwsze wystąpienia — każdy plik nadal jest na liście.
            if (rows.Count >= MaxRows && seen.Contains(key))
                return;
            if (!ancestors.Add(key))
                return;
            rows.Add(new ProgressRow(key, depth));
            if (seen.Add(key))
                ordered.Add(byKey[key]);
            if (children.TryGetValue(key, out var list))
                foreach (var child in list)
                    Place(child, depth + 1);
            ancestors.Remove(key);
        }

        // Korzenie — to, co nie jest niczyim dzieckiem — w kolejności zgłoszenia.
        foreach (var entry in entries)
            if (!isChild.Contains(entry.Key))
                Place(entry.Key, 0);

        // Zabezpieczenie: cokolwiek nieosiągalne z korzeni (cykl w danych) i tak trafia na listę,
        // płasko na końcu. Lista bez którejś pozycji kłamałaby przy liczniku "3 z 7".
        foreach (var entry in entries)
            if (!seen.Contains(entry.Key))
                Place(entry.Key, 0);

        return (ordered, rows);
    }
}

// Jeden wiersz panelu postępu: który plik i na jakim poziomie wcięcia (0 = najwyższy).
record ProgressRow(string Key, int Depth);
