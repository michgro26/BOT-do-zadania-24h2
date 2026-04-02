# BuildVersionBot

Osobny program do cyklicznego sprawdzania wersji buildu systemu operacyjnego na stacjach z tabeli `dbo.OHD_24h2`.

## Co robi
- łączy się z bazą przez `secureconn.dat`
- pobiera `COMPUTER_NAME` z rekordów, gdzie:
  - `DESCRIPTION = 'Do realizacji'`
  - `LAST_SCAN` jest puste lub starsze niż `PERIOD` godzin
  - albo `RESULT = 'OFFLINE'`
- sprawdza dostępność hosta
- odczytuje build z rejestru zdalnego:
  - `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`
- zapisuje do bazy:
  - `LAST_SCAN`
  - `OPERATOR = 'Hades2BotBuildVersion'`
  - `RESULT = OFFLINE / Windows xx, yy / BŁĄD`
  - `DESCRIPTION = 'Zrobione'` tylko gdy `RESULT = 'Windows 11, 24h2'`

## Tryby pracy
### Normalny
Uruchomienie bez parametrów:
- program działa w pętli
- kończy pracę o `END_HOUR`
- co `DELAY` minut wykonuje kolejny cykl

### Verbose
Uruchomienie:
```powershell
BuildVersionBot.exe -verbose computers.txt
```
- czyta stacje z pliku `computers.txt`
- bada tylko te stacje
- ignoruje wcześniejsze warunki `DESCRIPTION` / `LAST_SCAN`
- wykonuje tylko jeden cykl i kończy działanie

## Ważne
Folder `Security` zawiera placeholder dla `AesConnectionStringProvider`.
Podmień go na działającą wersję z poprzedniego projektu, który już odszyfrowuje `secureconn.dat`.

## Logi
Logi są zapisywane w katalogu `LOG`, jeden plik na dzień:
```text
LOG\log_YYYYMMDD.txt
```
