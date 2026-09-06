import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:isolate';
import 'dart:math';

import 'package:ffi/ffi.dart';

import 'models.dart';

typedef _CreateNative = Int32 Function(
    Pointer<Utf8>, Int32, Pointer<Pointer<Void>>);
typedef _CreateDart = int Function(Pointer<Utf8>, int, Pointer<Pointer<Void>>);
typedef _GenerateNative = Int32 Function(
    Pointer<Void>, Pointer<Utf8>, Int32, Pointer<Pointer<Utf8>>);
typedef _GenerateDart = int Function(
    Pointer<Void>, Pointer<Utf8>, int, Pointer<Pointer<Utf8>>);
typedef _FreeTextNative = Void Function(Pointer<Utf8>);
typedef _FreeTextDart = void Function(Pointer<Utf8>);
typedef _DestroyNative = Void Function(Pointer<Void>);
typedef _DestroyDart = void Function(Pointer<Void>);

class LocalLlmWorker {
  LocalLlmWorker._(this._commands, this._responses, this._isolate) {
    _subscription = _responses.listen(_onResponse);
  }
  final SendPort _commands;
  final ReceivePort _responses;
  final Isolate _isolate;
  late final StreamSubscription<dynamic> _subscription;
  final Map<int, Completer<String>> _pending = {};
  int _nextId = 1;
  Future<void>? _disposing;

  static Future<LocalLlmWorker> start(String modelPath,
      {int threads = 4}) async {
    final ready = ReceivePort();
    final responses = ReceivePort();
    final isolate = await Isolate.spawn(_llmWorkerMain,
        (ready.sendPort, responses.sendPort, modelPath, threads));
    final message = await ready.first;
    if (message is! SendPort) {
      isolate.kill(priority: Isolate.immediate);
      responses.close();
      throw StateError(message.toString());
    }
    return LocalLlmWorker._(message, responses, isolate);
  }

  Future<String> generate(String prompt, {int maxTokens = 1024}) {
    final id = _nextId++;
    final completer = Completer<String>();
    _pending[id] = completer;
    _commands.send({'id': id, 'prompt': prompt, 'maxTokens': maxTokens});
    return completer.future;
  }

  void _onResponse(dynamic raw) {
    if (raw is! Map) return;
    final id = raw['id'];
    if (id is! int) return;
    final completer = _pending.remove(id);
    if (completer == null) return;
    final error = raw['error'];
    if (error != null) {
      completer.completeError(StateError(error.toString()));
    } else {
      completer.complete(raw['text']?.toString() ?? '');
    }
  }

  Future<void> dispose() => _disposing ??= _dispose();
  Future<void> _dispose() async {
    final reply = ReceivePort();
    _commands.send({'dispose': true, 'reply': reply.sendPort});
    await reply.first;
    reply.close();
    for (final completer in _pending.values) {
      completer.completeError(StateError('本機 LLM 已停止'));
    }
    _pending.clear();
    await _subscription.cancel();
    _responses.close();
    _isolate.kill(priority: Isolate.beforeNextEvent);
  }
}

void _llmWorkerMain((SendPort, SendPort, String, int) args) {
  final library = DynamicLibrary.open('libcanto_llm.so');
  final create =
      library.lookupFunction<_CreateNative, _CreateDart>('canto_llm_create');
  final generate = library
      .lookupFunction<_GenerateNative, _GenerateDart>('canto_llm_generate');
  final freeText = library
      .lookupFunction<_FreeTextNative, _FreeTextDart>('canto_llm_free_text');
  final destroy =
      library.lookupFunction<_DestroyNative, _DestroyDart>('canto_llm_destroy');
  final nativePath = args.$3.toNativeUtf8();
  final output = calloc<Pointer<Void>>();
  final status = create(nativePath, args.$4, output);
  calloc.free(nativePath);
  if (status != 0) {
    calloc.free(output);
    args.$1.send('本機 LLM 載入失敗（$status）');
    return;
  }
  final handle = output.value;
  calloc.free(output);
  final commands = ReceivePort();
  args.$1.send(commands.sendPort);
  commands.listen((raw) {
    if (raw is! Map) return;
    if (raw['dispose'] == true) {
      destroy(handle);
      commands.close();
      (raw['reply'] as SendPort).send(null);
      return;
    }
    final id = raw['id'];
    final prompt = raw['prompt'];
    if (id is! int || prompt is! String) return;
    final nativePrompt = prompt.toNativeUtf8();
    final nativeOutput = calloc<Pointer<Utf8>>();
    try {
      final code = generate(handle, nativePrompt,
          (raw['maxTokens'] as int?) ?? 1024, nativeOutput);
      if (code != 0) {
        args.$2.send({'id': id, 'error': '本機 LLM 推理失敗（$code）'});
      } else {
        final text = nativeOutput.value.toDartString();
        freeText(nativeOutput.value);
        args.$2.send({'id': id, 'text': text});
      }
    } catch (error) {
      args.$2.send({'id': id, 'error': error.toString()});
    } finally {
      calloc.free(nativePrompt);
      calloc.free(nativeOutput);
    }
  });
}

typedef PromptGenerator = Future<String> Function(String prompt,
    {int maxTokens});

class HierarchicalMeetingSummarizer {
  const HierarchicalMeetingSummarizer(this.generate,
      {this.maxChunkCharacters = 3500, this.fanIn = 4});
  final PromptGenerator generate;
  final int maxChunkCharacters;
  final int fanIn;

  static const _schema =
      '{"summary":"","topics":[],"decisions":[],"actionItems":[{"text":"","owner":null,"dueDate":null}],"followUps":[],"unresolvedQuestions":[],"risks":[]}';

  Future<MeetingReport> summarize(List<TranscriptSegment> segments,
      {required bool simplified}) async {
    final chunks = _chunks(segments);
    if (chunks.isEmpty) return MeetingReport.empty();
    var reports = <Map<String, dynamic>>[];
    for (final chunk in chunks) {
      reports.add(await _structured(
          _prompt('從以下逐字稿片段抽取事實。不可杜撰負責人、日期或決定；缺失值必須為 null。', chunk, simplified),
          simplified: simplified));
    }
    while (reports.length > 1) {
      final next = <Map<String, dynamic>>[];
      for (var index = 0; index < reports.length; index += fanIn) {
        final group =
            reports.sublist(index, min(index + fanIn, reports.length));
        next.add(await _structured(
            _prompt('合併並去除以下分段會議事實中的重複項目。只保留有證據的內容，缺失負責人或日期維持 null。',
                jsonEncode(group), simplified),
            simplified: simplified));
      }
      reports = next;
    }
    return MeetingReport.fromJson(reports.single);
  }

  List<String> _chunks(List<TranscriptSegment> segments) {
    final result = <String>[];
    var current = StringBuffer();
    for (final segment in segments) {
      final text = segment.displayText.trim();
      if (text.isEmpty) continue;
      final line = '[片段 ${segment.sequence + 1}] $text\n';
      if (current.length > 0 &&
          current.length + line.length > maxChunkCharacters) {
        result.add(current.toString());
        current = StringBuffer();
      }
      if (line.length <= maxChunkCharacters) {
        current.write(line);
      } else {
        for (var offset = 0;
            offset < line.length;
            offset += maxChunkCharacters) {
          result.add(line.substring(
              offset, min(offset + maxChunkCharacters, line.length)));
        }
      }
    }
    if (current.length > 0) result.add(current.toString());
    return result;
  }

  String _prompt(String instruction, String content, bool simplified) =>
      '<|im_start|>system\n你是完全離線的會議資料抽取引擎。只輸出有效 JSON，不要 markdown，不要思考過程。'
      '${simplified ? '使用简体中文。' : '使用香港繁體中文。'} 結構及欄位次序必須是：$_schema。'
      '只有明確表示已決定的內容才放 decisions；只有明確工作才放 actionItems；「未指定」不是決定或工作。'
      '出現「未決定／待確認」的事項必須放 unresolvedQuestions。不可把某項工作的負責人套到其他事項。'
      'actionItems 的 owner 或 dueDate 沒有逐字稿證據時必須是 null。<|im_end|>\n'
      '<|im_start|>user\n$instruction\n$content\n/no_think<|im_end|>\n'
      '<|im_start|>assistant\n';

  Future<Map<String, dynamic>> _structured(String prompt,
      {required bool simplified}) async {
    var raw = await generate(prompt, maxTokens: 1200);
    for (var attempt = 0; attempt < 2; attempt++) {
      try {
        raw = raw.replaceAll(RegExp(r'<think>[\s\S]*?</think>'), '').trim();
        final start = raw.indexOf('{');
        final end = raw.lastIndexOf('}');
        if (start < 0 || end <= start) {
          throw const FormatException('JSON missing');
        }
        final decoded = jsonDecode(raw.substring(start, end + 1));
        if (decoded is Map<String, dynamic>) return decoded;
      } catch (_) {
        if (attempt == 0) {
          raw = await generate(
              _prompt('修正以下內容為指定結構的有效 JSON；不要新增事實。', raw, simplified),
              maxTokens: 1200);
          continue;
        }
      }
    }
    throw const FormatException('本機 LLM 未能產生有效結構化會議資料');
  }
}
