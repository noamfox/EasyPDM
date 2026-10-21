import { useState } from "react"

// Kolejność listy ustawiana OD RAZU po przeciągnięciu, zanim serwer potwierdzi zmianę.
//
// Bez tego lista zmieniała się dopiero po odpowiedzi serwera i ponownym pobraniu danych, a
// dnd-kit w chwili puszczenia widzi element jeszcze na starym miejscu -- animacja pokazywała
// powrót elementu tam, skąd go wzięto, a ułamek sekundy później lista przeskakiwała na
// właściwy układ (zgłoszone z praktyki: "animacja sugeruje powrót elementu na miejsce").
//
// apply() układa listę według zapamiętanej kolejności, dopóki ta pasuje do danych (ten sam
// zestaw elementów); gdy dane się zmienią w inny sposób, wraca do kolejności z danych.
export function useOptimisticOrder() {
  const [order, setOrder] = useState<string[] | null>(null)

  function apply<T>(list: T[], idOf: (entry: T) => string): T[] {
    if (!order || order.length !== list.length) return list
    const byId = new Map(list.map((entry) => [idOf(entry), entry]))
    const arranged = order.map((id) => byId.get(id))
    return arranged.every((entry) => entry !== undefined) ? (arranged as T[]) : list
  }

  // Ustawia kolejność od razu, wysyła ją na serwer i odświeża dane. Zapamiętana kolejność
  // znika po odświeżeniu (dane już ją mają) albo po błędzie (lista wraca do stanu z serwera).
  async function commit(next: string[], save: () => Promise<unknown>, refetch: () => unknown) {
    setOrder(next)
    try {
      await save()
      await refetch()
    } finally {
      setOrder(null)
    }
  }

  return { apply, commit }
}
