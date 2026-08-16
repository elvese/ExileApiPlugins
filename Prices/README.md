# Prices

Plugin ExileApi do automatycznego obniżania cen przedmiotów wystawionych w **Merchant Shop**.

## Zasady

- pełny przebieg jest wykonywany automatycznie co **5 minut**;
- plugin przechodzi przez wszystkie zakładki Merchant Shop;
- uwzględnia wyłącznie oferty wycenione w **Chaos Orb**;
- każda cena jest obniżana o **10%** (po zaokrągleniu w dół do pełnego Chaos Orba);
- cena nigdy nie spada poniżej **30% ceny początkowej** (limit jest zaokrąglany w górę);
- ceny początkowe są zapisywane w `PricesData.json`, dzięki czemu ponowne uruchomienie ExileApi nie resetuje limitu;
- ręczne podniesienie ceny powyżej zapisanej ceny początkowej rozpoczyna dla przedmiotu nowy cykl;
- ręczna cena niższa od zapisanego minimum nie jest przez plugin podnoszona.

Przykład dla ceny początkowej `100 chaos`:

`100 -> 90 -> 81 -> 72 -> 64 -> 57 -> 51 -> 45 -> 40 -> 36 -> 32 -> 30`

## Użycie

1. Włącz plugin `Prices` w ExileApi.
2. Otwórz panel Merchant Shop i pozostaw grę na pierwszym planie.
3. Plugin wykona przebieg po upływie 5 minut. Klawisz `F8` uruchamia przebieg ręcznie.
4. Przytrzymanie prawego przycisku myszy, zamknięcie panelu lub utrata fokusu gry przerywa przebieg.

Jeżeli panel jest zamknięty w chwili upływu czasu, przebieg pozostaje oczekujący i rozpocznie się po otwarciu panelu. Plugin bezpiecznie pomija przedmioty tymczasowo zablokowane przez grę po niedawnej zmianie ceny.

> Plugin steruje myszą i klawiaturą. Tego typu automatyzacja może naruszać regulamin gry i wiąże się z ryzykiem blokady konta. Używasz jej na własną odpowiedzialność.
