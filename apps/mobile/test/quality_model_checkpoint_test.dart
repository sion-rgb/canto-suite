import 'package:canto_meet/core/meeting_store.dart';
import 'package:canto_meet/core/models.dart';
import 'package:canto_meet/core/quality_transcription.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

class CheckpointStore extends MeetingStore {
  String identity = 'model-a/revision-a';
  String formal = 'previous quality text';
  final raw = 'original live text';
  @override
  Future<List<TranscriptSegment>> segments(String meetingId) async => [
        TranscriptSegment(
            id: 'segment',
            meetingId: meetingId,
            sequence: 0,
            audioPath: 'fixture.m4a',
            rawText: raw,
            cleanedText: formal,
            state: 'quality_complete')
      ];
  @override
  Future<String?> qualityModelIdentity(String segmentId) async => identity;
  @override
  Future<void> setMeetingState(String meetingId, MeetingState state) async {}
  @override
  Future<void> saveQualityTranscript(String segmentId, String formalText,
      {String? modelIdentity}) async {
    identity = modelIdentity!;
    formal = formalText;
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test('quality resume reuses only the same pinned model identity', () async {
    const channel = MethodChannel('hk.canto.canto_meet/audio_control');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
            channel, (call) async => call.arguments['text']);
    addTearDown(() => TestDefaultBinaryMessengerBinding
        .instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, null));
    final store = CheckpointStore();
    final loaded = <String>[];
    final transcriber = QualityTranscriber(store,
        transcribeFile: (path, model, acknowledgement) async {
      loaded.add(model);
      await acknowledgement?.call();
      return 'quality from $model';
    });
    await transcriber.runMeeting('meeting', store.identity, simplified: false);
    expect(loaded, isEmpty);
    await transcriber.runMeeting('meeting', 'model-b/revision-b',
        simplified: false);
    expect(loaded, ['model-b/revision-b']);
    expect(store.identity, 'model-b/revision-b');
    expect(store.formal, 'quality from model-b/revision-b');
    expect(store.raw, 'original live text');
    await transcriber.runMeeting('meeting', store.identity, simplified: false);
    expect(loaded.length, 1);
  });
}
