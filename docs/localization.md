# Localization

The interface ships with 15 languages selected by estimated total speakers, counting first- and second-language speakers. The selection follows the 2026 Ethnologue 200 ranking. [Ethnologue 200](https://shop.ethnologue.com/products/2026-ethnologue-200)

| Locale | Language |
| --- | --- |
| `en` | English |
| `zh-Hans` | Mandarin Chinese in simplified script |
| `hi` | Hindi |
| `es` | Spanish |
| `ar` | Modern Standard Arabic |
| `fr` | French |
| `bn` | Bengali |
| `pt` | Portuguese |
| `id` | Indonesian |
| `ur` | Urdu |
| `ru` | Russian |
| `de` | German |
| `ja` | Japanese |
| `pcm` | Nigerian Pidgin |
| `arz` | Egyptian Arabic |

The initial language follows Windows when supported, with English as the fallback. The language picker updates the interface and saves the choice per data directory. Arabic, Egyptian Arabic and Urdu use a right-to-left layout. Windows file pickers, message box buttons, VMConnect and raw Hyper-V diagnostics use Windows or provider language settings.

## Updating translations

Edit the UTF-8 JSON files in `locales/`. Add the key to English and each locale. Keep placeholders such as `{0}` and `{1}` intact, and reorder them to suit the language. Use `TextCatalog.Format` for messages with variables. Run the .NET tests to verify matching key sets, placeholders and WPF language switching.

Use the Translation correction issue form to give the locale, VMCrate version, screen and string key. Run the desktop localization checks at a compact window size and review right-to-left layout for Arabic, Egyptian Arabic and Urdu. The initial translations are open for language review. Include the locale and the screen name when proposing a correction.
