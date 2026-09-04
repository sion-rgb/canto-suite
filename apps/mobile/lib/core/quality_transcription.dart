import 'dart:async';
import 'dart:typed_data';

import 'package:canto_core/canto_core.dart';
import 'package:flutter/services.dart';

import 'meeting_store.dart';
import 'models.dart';
import 'pcm_bytes.dart';

class ChineseOutputConverter {
  static const MethodChannel _channel =
      MethodChannel('hk.canto.canto_meet/audio_control');

  static Future<String> convert(String text,
          {required bool simplified, bool clean = false}) async =>
      (await _channel.invokeMethod<String>('convertChinese',
          {'text': text, 'simplified': simplified, 'clean': clean})) ??
      text;
}

class QualityTranscriber {
  static const MethodChannel _control =
      MethodChannel('hk.canto.canto_meet/audio_control');
  static const EventChannel _events =
      EventChannel('hk.canto.canto_meet/audio_events');

  const QualityTranscriber(this.store);
  final MeetingStore store;

  Future<void> runMeeting(String meetingId, String modelPath,
      {required bool simplified,
      void Function(int completed, int total)? onProgress}) async {
    final segments = await store.segments(meetingId);
    await store.setMeetingState(meetingId, MeetingState.transcribing);
    for (var index = 0; index < segments.length; index++) {
      final existing = segments[index];
      if (existing.state == 'quality_complete' &&
          (existing.cleanedText?.trim().isNotEmpty ?? false)) {
        onProgress?.call(index + 1, segments.length);
        continue;
      }
      final text = await _transcribeFile(existing.audioPath, modelPath);
      final formal = await ChineseOutputConverter.convert(text,
          simplified: simplified, clean: true);
      await store.saveQualityTranscript(existing.id, formal);
      onProgress?.call(index + 1, segments.length);
    }
  }

  Future<String> _transcribeFile(String path, String modelPath) async {
    final worker = await CantoCoreWorker.start();
    final results = <String>[];
    final finalResult = Completer<void>();
    Timer? pollTimer;
    late StreamSubscription<TranscriptResult> resultSubscription;
    late StreamSubscription<dynamic> audioSubscription;
    resultSubscription = worker.results.listen((result) {
      if (result.kind == TranscriptKind.error) {
        if (!finalResult.isCompleted) {
          finalResult.completeError(StateError(result.text));
        }
      } else if (result.text.trim().isNotEmpty) {
        // canto-core uses `partial` for complete, non-overlapping chunks and
        // marks only the end-of-stream chunk final. Retain every chunk so a
        // quality pass does not silently lose the beginning of a segment.
        results.add(result.text.trim());
        if (result.kind == TranscriptKind.finalResult &&
            !finalResult.isCompleted) {
          finalResult.complete();
        }
      }
    });
    audioSubscription = _events.receiveBroadcastStream().listen((raw) {
      if (raw is! Map) return;
      final event = Map<Object?, Object?>.from(raw);
      if (event['type'] == 'decode_pcm' && event['data'] is Uint8List) {
        final bytes = event['data'] as Uint8List;
        worker.push(pcm16LittleEndianView(bytes));
      } else if (event['type'] == 'decode_done') {
        worker.push(Int16List(0), endOfStream: true);
        pollTimer = Timer.periodic(
            const Duration(milliseconds: 100), (_) => worker.poll());
      } else if (event['type'] == 'decode_error' && !finalResult.isCompleted) {
        finalResult.completeError(
            StateError(event['message']?.toString() ?? '錄音解碼失敗'));
      }
    });
    try {
      worker.loadModel(modelPath);
      await _control.invokeMethod<void>('decode', {'path': path});
      await finalResult.future.timeout(const Duration(minutes: 8));
      return results.join('\n');
    } finally {
      pollTimer?.cancel();
      await audioSubscription.cancel();
      await resultSubscription.cancel();
      worker.dispose();
    }
  }
}
