# BuildVersionBot

Osobny program do cyklicznego sprawdzania wersji buildu systemu operacyjnego na stacjach z tabeli `dbo.OHD_24h2`.

## Co robi
- ³¹czy siê z baz¹ przez `secureconn.dat`
- pobiera `COMPUTER_NAME` z rekordów, gdzie:
  - `DESCRIPTION = 'Do realizacji'`
  - `LAST_SCAN` jest puste lub starsze ni¿ `PERIOD` godzin
  - albo `RESULT = 'OFFLINE'`
- sprawdza dostêpnoœæ hosta
- odczytuje build z rejestru zdalnego:
  - `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`
- zapisuje do bazy:
  - `LAST_SCAN`
  - `OPERATOR = 'Hades2BotBuildVersion'`
  - `RESULT = OFFLINE / Windows xx, yy / B£¥D`
  - `DESCRIPTION = 'Zrobione'` tylko gdy `RESULT = 'Windows 11, 24h2'`

## Tryby pracy
### Normalny
Uruchomienie bez parametrów:
- program dzia³a w pêtli
- koñczy pracê o `END_HOUR`
- co `DELAY` minut wykonuje kolejny cykl

### Verbose
Uruchomienie:
```powershell
BuildVersionBot.exe -verbose computers.txt
```
- czyta stacje z pliku `computers.txt`
- bada tylko te stacje
- ignoruje wczeœniejsze warunki `DESCRIPTION` / `LAST_SCAN`
- wykonuje tylko jeden cykl i koñczy dzia³anie

## Wa¿ne
Folder `Security` zawiera placeholder dla `AesConnectionStringProvider`.
Podmieñ go na dzia³aj¹c¹ wersjê z poprzedniego projektu, który ju¿ odszyfrowuje `secureconn.dat`.

## Logi
Logi s¹ zapisywane w katalogu `LOG`, jeden plik na dzieñ:
```text
LOG\log_YYYYMMDD.txt
```

## Przy uruchomieniu -verbose computer.txt program ma usun¹æ z pliku computer.txt te wiersze, dla których build jest zgodny z oczekiwanym. Czyli:
- mam w pliku A0001-OSWIECIM
- program-bot go sprawdzi³ i stwierdzi³ ¿e jest tam 24h2
- zapisuje to w bazie (dzia³a zgodnie z oczekiwaniami)
- i dodatkowo ma usun¹æ ten komputer z pliku computers.txt