import 'dart:async';
import 'dart:io';
import 'dart:typed_data';
import 'package:flutter/services.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:record/record.dart';

import 'meeting_store.dart';

class SegmentedRecorder {
  SegmentedRecorder(this._store,
      {this.segmentDuration = const Duration(minutes: 5)});
  final MeetingStore _store;
  final Duration segmentDuration;
  final AudioRecorder _permissionRecorder = AudioRecorder();
  final StreamController<Int16List> _pcm = StreamController.broadcast();
  static const MethodChannel _control =
      MethodChannel('hk.canto.canto_meet/audio_control');
  static const EventChannel _events =
      EventChannel('hk.canto.canto_meet/audio_events');
  StreamSubscription<dynamic>? _eventSubscription;
  String? _meetingId;
  final Set<int> _committedSequences = {};
  final List<Future<void>> _pendingCommits = [];

  Stream<Int16List> get pcm16 => _pcm.stream;

  Future<void> start(String meetingId) async {
    if (!await _permissionRecorder.hasPermission()) {
      throw StateError('需要咪高峰權限先可以錄音');
    }
    _meetingId = meetingId;
    final support = await getApplicationSupportDirectory();
    final directory = Directory(p.join(support.path, 'meetings', meetingId));
    await directory.create(recursive: true);
    _eventSubscription = _events
        .receiveBroadcastStream()
        .listen(_onAudioEvent, onError: (Object error) => _pcm.addError(error));
    await _control.invokeMethod<void>('start', {
      'directory': directory.path,
      'meetingId': meetingId,
      'segmentDurationMs': segmentDuration.inMilliseconds,
    });
  }

  void _onAudioEvent(dynamic rawEvent) {
    if (rawEvent is! Map) return;
    final event = Map<Object?, Object?>.from(rawEvent);
    switch (event['type']) {
      case 'pcm':
        final data = event['data'];
        if (data is Uint8List && data.lengthInBytes.isEven) {
          _pcm.add(Int16List.view(
              data.buffer, data.offsetInBytes, data.lengthInBytes ~/ 2));
        }
        break;
      case 'segment':
        final path = event['path'];
        final sequence = event['sequence'];
        if (path is String && sequence is int) {
          _pendingCommits.add(_commit(sequence, path));
        }
        break;
      case 'error':
        _pcm.addError(StateError(event['message']?.toString() ?? '錄音失敗'));
        break;
    }
  }

  Future<void> _commit(int sequence, String audioPath) async {
    if (!_committedSequences.add(sequence) || _meetingId == null) return;
    final segmentId = '${_meetingId!}-$sequence';
    await _store.commitSegment(
        id: segmentId,
        meetingId: _meetingId!,
        sequence: sequence,
        audioPath: audioPath);
  }

  Future<void> stop() async {
    final paths = (await _control.invokeMethod<List<dynamic>>('stop')) ?? [];
    for (final rawPath in paths) {
      if (rawPath is! String) continue;
      final match = RegExp(r'segment_(\d+)\.m4a$').firstMatch(rawPath);
      if (match != null) {
        _pendingCommits.add(_commit(int.parse(match.group(1)!), rawPath));
      }
    }
    await Future.wait(_pendingCommits);
    _pendingCommits.clear();
    await _eventSubscription?.cancel();
    _eventSubscription = null;
    _meetingId = null;
  }

  Future<void> dispose() async {
    await _eventSubscription?.cancel();
    await _permissionRecorder.dispose();
    await _pcm.close();
  }
}
