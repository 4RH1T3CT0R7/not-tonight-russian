# Not Tonight — Русская локализация

Полная русификация игры **Not Tonight** (базовая игра + DLC "One Love").

## Установка

### Автоматическая (рекомендуется)

1. Скачайте `NotTonightRussian-Setup.exe` из [последнего релиза](https://github.com/4RH1T3CT0R7/not-tonight-russian/releases/latest)
2. Запустите установщик
3. Укажите папку с игрой (обычно определяется автоматически)
4. Нажмите "Установить"

### Ручная

1. Скачайте `NotTonightRussian-Data.zip` из [последнего релиза](https://github.com/4RH1T3CT0R7/not-tonight-russian/releases/latest)
2. Распакуйте содержимое в папку с игрой (где находится `Not Tonight.exe`)

## Что устанавливается

- **BepInEx** — фреймворк модов для Unity
- **XUnity.AutoTranslator** — система перевода текста
- **NotTonightRussian.dll** — плагин русификации (I2 Localization + динамический перевод)
- **Переводы** — 8,000+ ключей I2 + XUnity (диалоги, интерфейс, предметы, описания)

## Удаление

Запустите установщик и нажмите "Удалить мод", либо удалите папку `BepInEx` и файлы `winhttp.dll`, `doorstop_config.ini` из папки с игрой.

## О переводе

- **Всего**: 6,595 уникальных пар EN->RU (8,037 ключей)
- **Базовая игра**: 7,634 строки
- **DLC One Love**: 985 строк
- **Покрытие**: 100% всех текстовых строк
- Сохранен темный юмор и сатирический тон оригинала

## Сборка из исходников

```bash
dotnet build src/Mod/NotTonightRussian.csproj -c Release
```

Для сборки установщика:
```bash
build.bat --installer
```

## Автор

**Artem Lytkin** ([4RH1T3CT0R](https://github.com/4RH1T3CT0R7))
