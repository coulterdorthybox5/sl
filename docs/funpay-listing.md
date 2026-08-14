# Описание для FunPay-лота

Готовая заготовка, которую можно скопировать в поле «Описание» на FunPay.
Ниже — три варианта: краткий текст, полное описание и BBCode для форумов
FunPay (`[b]`, `[url]`, `[list]`).

---

## 1) Короткий вариант (для карточки-лота)

```
API-шлюз к 10 моделям Anthropic, OpenAI, Google, xAI через один OpenAI-совместимый
эндпоинт. Работает с Claude Code, Codex CLI, Cline, Continue, LibreChat и т.д.

- Один Base URL, один ключ - все модели сразу.
- Баланс: 1 000 000 кредитов = 25 $. Списание: tokens × коэффициент модели
  (0.5x - 2.0x). Дешёвые модели (Luna, Gemini Flash) - x0.5, топовые (Claude
  Fable, GPT-5.6 Sol) - до x2.0.
- Стрим (SSE), полная OpenAI-совместимость, ротация ключей за кулисами.

Сайт и туториал: https://agree-best-delhi-cpu.trycloudflare.com/
После покупки: вставь свой ключ на странице выше -> "Проверить" -> получишь
статус, баланс и готовые сниппеты для Claude Code / Codex / OpenAI-compat.
```

---

## 2) Полное описание (для длинного поста / вкладки «О товаре»)

```
LLM Gateway - один API-ключ к 10 моделям сразу
================================================

ЧТО ЭТО
- OpenAI-совместимый прокси. Один Base URL, один ключ - работает
  везде, где можно задать base_url: Claude Code, Codex CLI, Cline,
  Continue, LibreChat, LobeChat, TypingMind, Roo Code и любой SDK
  (openai-python, openai-node, LangChain, LlamaIndex и т.д.).
- Стриминг (SSE) поддерживается, обычный JSON тоже.
- За кулисами - ротация нескольких провайдерских ключей. Если один
  ключ выдохся, следующий подхватит запрос автоматически, клиент
  ничего не замечает.

ДОСТУПНЫЕ МОДЕЛИ (10 штук, все идут по единому ключу)

Anthropic:
  claude-fable-5      x2.0    ($10 / $50 за 1M у провайдера)
  claude-opus-4-8     x1.5    ($5  / $25)
  claude-opus-4-7     x1.5    ($5  / $25)
  claude-sonnet-5     x1.0    ($3  / $10)

OpenAI:
  gpt-5-6-sol         x1.7    ($5  / $30)
  gpt-5-5             x1.5    ($5  / $25)
  gpt-5-6-terra       x1.2    ($3  / $15)
  gpt-5-6-luna        x0.5    ($1  / $2)

Google:
  gemini-3-5-flash    x0.5    ($0.5 / $2)

xAI:
  grok-4-5            x0.8    ($2  / $6)

БАЛАНС И ТАРИФИКАЦИЯ
- 1 000 000 кредитов = 25 $ (базовый пакет).
- За каждый токен списывается tokens × коэффициент модели.
  Пример: 1 000 токенов Claude Fable 5 (x2.0)  = 2 000 кредитов.
          1 000 токенов GPT-5.6 Luna  (x0.5) = 500 кредитов.
- Минимальный коэффициент 0.5, максимум 2.0.
- Расход виден в реальном времени на странице статуса ключа.

БЕЗОПАСНОСТЬ
- HTTPS через Cloudflare Tunnel, без прямого IP сервера.
- Anti-abuse: rate-limit по IP, защита панели от brute-force,
  ограничение размера тела запроса, ограничение размера prompt.
- Ключи провайдера в базе - AES-256-GCM, в открытом виде хранится
  только hash клиентского ключа.

САЙТ
- Главная и модели:
    https://agree-best-delhi-cpu.trycloudflare.com/
- Проверка ключа + туториал (Claude Code / Codex / OpenAI-compat):
    https://agree-best-delhi-cpu.trycloudflare.com/check
- Пригласительная страница твоего ключа (без ввода, сразу статус):
    https://agree-best-delhi-cpu.trycloudflare.com/k/<public_id>

КАК ПОДКЛЮЧИТЬ (кратко)
1. Открой сайт, вставь свой ключ, нажми "Проверить".
2. На открывшейся странице выбери свой клиент - Claude Code,
   Codex или "OpenAI-compatible". Скопируй готовый сниппет.
3. Base URL для любых OpenAI-SDK:
     https://agree-best-delhi-cpu.trycloudflare.com/v1

ЧТО НУЖНО ЗНАТЬ ПЕРЕД ПОКУПКОЙ
- Ключ выдаётся один раз, после покупки в файле - его нельзя
  восстановить из базы (там только hash).
- Срок действия - настраиваемый (по умолчанию 30 дней).
- Возврат/обмен - только если ключ ни разу не использовался.

ПОДДЕРЖКА
Пиши в личку FunPay. Отвечаю в течение дня.
```

---

## 3) BBCode-вариант (для форума FunPay)

```
[b]LLM Gateway - один ключ к 10 моделям[/b]

OpenAI-совместимый API. Работает с Claude Code, Codex CLI, Cline,
Continue, LibreChat, LobeChat, любой SDK через свой base URL.

[b]Модели:[/b]
[list]
[*] Anthropic: [b]claude-fable-5[/b] x2.0, claude-opus-4-8 x1.5, claude-opus-4-7 x1.5, claude-sonnet-5 x1.0
[*] OpenAI: gpt-5-6-sol x1.7, gpt-5-5 x1.5, gpt-5-6-terra x1.2, [b]gpt-5-6-luna x0.5[/b]
[*] Google: [b]gemini-3-5-flash x0.5[/b]
[*] xAI: grok-4-5 x0.8
[/list]

[b]Баланс:[/b] 1 000 000 кредитов = 25 $. Списание = tokens × коэффициент модели.
Коэффициент от 0.5x до 2.0x - топовые модели дороже, лёгкие дешевле.

[b]Сайт:[/b] [url]https://agree-best-delhi-cpu.trycloudflare.com/[/url]
[b]Проверить ключ + туториал:[/b] [url]https://agree-best-delhi-cpu.trycloudflare.com/check[/url]
[b]Base URL для SDK:[/b] https://agree-best-delhi-cpu.trycloudflare.com/v1

[b]Как подключить:[/b]
[list=1]
[*] Открой /check, вставь ключ.
[*] Выбери свой клиент (Claude Code / Codex / OpenAI-compat).
[*] Скопируй сниппет со страницы - готово.
[/list]

Стриминг, ротация ключей провайдера, HTTPS через Cloudflare.
Ключ действителен 30 дней по умолчанию, восстановлению не подлежит - храни файл.
```

---

## Заметки

- Trycloudflare-адрес меняется при каждом рестарте туннеля.
  Если сменишь на постоянный (Cloudflare Named Tunnel или свой домен),
  обнови все три URL в этом файле и в описании лота.
- Если правишь количество моделей или коэффициенты - подправь и здесь
  (`llm-gateway/gateway/models_catalog.py` - источник правды).
