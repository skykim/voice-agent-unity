# Nova, an on-device voice agent for Unity

[![Nova](https://img.youtube.com/vi/E_TsfNiE5fo/0.jpg)](https://youtu.be/E_TsfNiE5fo)

Nova is a voice assistant that runs entirely inside Unity 6 with the Inference Engine (Sentis).
You tap the orb and speak. It then:

- turns your speech into text,
- works out which of 19 commands you meant, with probabilities shown live as you talk,
- runs the command in a small simulated home, or answers from the internet,
- replies out loud.

It understands **English and Korean** and answers in the language you used.

| Stage | Model | Runs on | Source |
|---|---|---|---|
| Voice activity | Silero VAD (FP16) | CPU | [`com.sky.sentis.silero-vad`](https://huggingface.co/Sky-Kim/com.sky.sentis.silero-vad) |
| Speech to text | SenseVoice-Small (FP16), auto language | GPU | [`com.sky.sentis.sensevoice`](https://huggingface.co/Sky-Kim/com.sky.sentis.sensevoice) |
| Intent | gemma-3-270m-it (FP16), frozen, plus a Decision AI attention head | GPU + CPU | [`com.sky.sentis.gemma3-270m-it`](https://huggingface.co/Sky-Kim/com.sky.sentis.gemma3-270m-it) + `Editor/Training/` |
| Chat fallback | gemma-3-270m-it greedy generation (same graph, KV cache) | GPU | same package |
| Text to speech | Supertonic 3 (FP16), voice F2 | GPU | [`com.sky.sentis.supertonic`](https://huggingface.co/Sky-Kim/com.sky.sentis.supertonic) |

Typical latency from the end of speech to the first sound of the reply on an Apple M5 Max:

- Home commands: about 0.25 s (SenseVoice ≈ 40 ms, intent ≈ 20 ms, Supertonic ≈ 160 ms).
- Internet answers: 1–3 s, mostly network time.

---

## Getting started

**Requirements**

- Unity **6000.6.1f1** (URP).
- [git-lfs](https://git-lfs.com). The four model packages are Git repositories on Hugging Face with LFS model files.

**Run the demo.** Clone the repository and open it in Unity. The Package Manager fetches the FP16 models from Hugging Face (about 1.4 GB in total, the first time only). Then open `Assets/Scenes/VoiceAgentDemo.unity`, press Play, and tap the orb or type.

The trained intent head (`Assets/StreamingAssets/Intent/decision_ai_head.sentis`) is included, so no training is needed. To retrain it, see [Training the intent head](#training-the-intent-head).

`ModelRoots` reads the models straight from each package's `Models~` folder in the Editor. Player builds (Android, iOS, desktop) get them in `StreamingAssets/Models`, staged for the build only; Android copies them out of the APK on first launch.

### Scenes

| Scene | What it shows |
|---|---|
| `VoiceAgentDemo` | The whole agent: microphone or typing, live command chips, home tiles, spoken replies, and a latency panel for every turn |
| `Tests/VadTest` | Silero speech probability per 32 ms frame, and speech segments |
| `Tests/SttTest` | SenseVoice partials and finals, the detected language and the real-time factor |
| `Tests/TtsTest` | Supertonic 3 in Korean and English with the ten voice styles |
| `Tests/GemmaTest` | Gemma3 270M alone: streamed chat generation under an editable system prompt, with its speed |
| `Tests/AgentTest` | All 19 command probabilities live, encoder and head timings, a generation test |

All UI lives in the scene files; edit it in the Editor.

---

## How it works

```
mic ──► Silero VAD ──► SenseVoice ──► text ──► Gemma (frozen) + Decision AI head ──► command ──► action or answer ──► Supertonic 3 ──► speaker
```

1. **Speech in.** `VoiceInput` streams the microphone through Silero VAD, and SenseVoice transcribes each utterance and detects whether it is English or Korean. Partial transcripts update the command chips while you are still talking.
2. **Choosing a command.** Gemma doesn't write a function call. It is used as a frozen encoder: one short prefill (about 20 ms) gives a state per token, and a small trained [jevlike](https://github.com/vinnylarouge/jevlike) head (`DecisionAIRanker`) scores those states against one option vector per command from `Resources/Commands.json`. `PolarityGuard` then fixes on/off and up/down mix-ups with the direction words in `Resources/Polarity.json`, and `TurnRouting` runs a confident pick, asks "Did you mean ...?" when unsure, or hands `chat` to Gemma generation.
3. **Acting and answering.** Home commands (lights, light color, TV, computer, vacuum, music, volume) change the simulated home in `SmartHome`. Weather, time and location come from Open-Meteo and ipwho.is. `web_search` asks Wikidata first, then ranks sentences from Wikipedia and news feeds, so internet answers are extracted, never written by the 270M model. Chat turns go to Gemma with Nova's persona from `Resources/Persona.json`.
4. **Speech out.** Supertonic 3 speaks the reply with voice F2, in Korean when the reply is mostly Hangul. The right-hand panel shows how long each stage of the turn took.

---

## FunctionGemma vs Decision AI

An [earlier version](https://github.com/skykim/mini-agent-unity) used [FunctionGemma](https://huggingface.co/google/functiongemma-270m-it), fine-tuned with LoRA ([Sky-Kim/functiongemma-270m-finetune](https://huggingface.co/Sky-Kim/functiongemma-270m-finetune)) to write function calls. This project replaces it with a Decision AI head on a frozen Gemma. Both use the same 270M Gemma 3 backbone.

| | FunctionGemma (LoRA) | Decision AI (frozen Gemma + head) |
|---|---|---|
| Trained parameters | ≈ 3.8 M | ≈ 0.25 M |
| Training time | ≈ 2.5 h on Apple MPS | ≈ 1 min in the Unity Editor |
| Inference per command | ≈ 500 ms (long prompt, then generate the call) | ≈ 20 ms (one short prefill + head) |
| Adding a command | Regenerate data, retrain, merge, export, convert | Edit `Commands.json` and the example phrases, retrain the head |

Decision AI leaves Gemma untouched, so one graph does intent, chat and search embeddings. The fine-tuned model changed every layer and mostly refused small talk. What Decision AI gives up is argument extraction and several calls in one sentence: it picks one command, and Nova fills in the few arguments it needs (city, color, Wikidata entity) with rules.

---

## Training the intent head

Only the Decision AI head is trained (about 250k parameters). Gemma stays frozen: it encodes each training sentence once, and the head trains on those cached states. Everything runs in the Unity Editor.

**VoiceAgent ▸ Train Command Head…** opens the trainer window: set the Gemma3 layer (12 by default), the head size, epochs and optimizer if you like, press **Train** (about a minute), and read the validation accuracy, the on/off check and the benchmark in the log.

The head is written to `Assets/StreamingAssets/Intent/decision_ai_head.sentis` as a Sentis graph (token states to command probabilities, run on the CPU, with the Gemma3 layer, token limit and command order stored in the graph) and the report to `Logs/train-command-head.json`.

The trainer first holds some sentences out to measure the head and pick the epoch count, then trains the final head on every sentence. It always trains on English and Korean and writes the runtime's head file.

**Training data**

- The English and Korean rows of the [functiongemma-270m-finetune](https://huggingface.co/Sky-Kim/functiongemma-270m-finetune) dataset, downloaded once (19 MB) into `Library/VoiceAgent/functiongemma/`.
- `Editor/Training/extra_phrases.json`, for what the dataset lacks (volume, "who are you", small talk, more Korean phrasings).
- Two evaluation sets that are never trained on: `polarity_eval.json` (64 on/off sentences) and `benchmark.json` (136 sentences across all commands).

**Adding a command**

1. Add it to `Resources/Commands.json`: label, icon, reply, and a few option phrases.
2. Add 20 to 40 example sentences to `Editor/Training/extra_phrases.json` under `phrases.<command id>`.
3. Make it do something in `SmartHome` or `InfoAgent`.
4. Retrain. The chips and the Agent Test's bars follow `Commands.json` by themselves.

---

## Project layout

```
Assets/VoiceAgent/
  Runtime/Audio/    microphone, speech input (VAD + STT), speech output (TTS), music
  Runtime/Ai/       the Decision AI head and the on/off guard
  Runtime/Core/     turn logic, routing, the simulated home, commands and persona
  Runtime/Info/     weather, time, location and web search
  Runtime/UI/       the assistant screen, chips, home tiles and latency panel
  Runtime/Scenes/   one script per scene
  Resources/        Commands.json, Persona.json, Polarity.json, Icons/
  UI/               sprites the scenes use
  Editor/Training/  the trainer window, the trainer, the head exporter and the training data
  Tests/            EditMode and PlayMode tests
Assets/StreamingAssets/Intent/   decision_ai_head.sentis (the trained head)
```

## Privacy and network

Speech recognition, intent, generation and speech synthesis all run locally. Only the information commands go online:

- Open-Meteo, for weather and time;
- ipwho.is, for location (it receives the machine's public IP);
- Wikidata, Wikipedia, DuckDuckGo, Google News and Bing, for web search.

## License

- [Gemma 3](https://ai.google.dev/gemma), under the [Gemma Terms of Use](https://ai.google.dev/gemma/terms), through [`com.sky.sentis.gemma3-270m-it`](https://huggingface.co/Sky-Kim/com.sky.sentis.gemma3-270m-it).
- [jevlike](https://github.com/vinnylarouge/jevlike) (Copyright (c) 2026 Minimal Labs), MIT. `HeadTrainer` implements its option-attention head and training in C#; `HeadExporter` writes it as a Sentis graph that `DecisionAIRanker` runs.
- The speech models also come through Sentis packages: Silero VAD, SenseVoice-Small (FunAudioLLM) and Supertonic 3 (Supertone). Each package states its model license.
- Data: Open-Meteo, Wikidata and Wikipedia (CC BY-SA), and news RSS feeds.
- Icons: [Material Icons](https://github.com/google/material-design-icons) by Google (Round style), under the [Apache License 2.0](Assets/VoiceAgent/Resources/Icons/LICENSE.txt), recolored to white (`Resources/Icons`).
