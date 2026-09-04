import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:sqflite/sqflite.dart';

import 'models.dart';

class MeetingStore {
  Database? _database;

  Future<Database> get database async {
    if (_database case final database?) return database;
    final support = await getApplicationSupportDirectory();
    final path = p.join(support.path, 'canto_meet.db');
    _database = await openDatabase(path,
        version: 2,
        onConfigure: (db) => db.execute('PRAGMA foreign_keys=ON'),
        onCreate: (db, _) async {
          await db.execute('''CREATE TABLE meetings(
        id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at INTEGER NOT NULL,
        state TEXT NOT NULL, duration_seconds INTEGER NOT NULL DEFAULT 0,
        summary_json TEXT, selected_profile TEXT NOT NULL)''');
          await db.execute('''CREATE TABLE segments(
        id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
        sequence INTEGER NOT NULL, audio_path TEXT NOT NULL, raw_text TEXT,
        cleaned_text TEXT, user_text TEXT, speaker_id TEXT, state TEXT NOT NULL,
        UNIQUE(meeting_id, sequence))''');
          await db.execute('''CREATE TABLE markers(
        id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
        segment_id TEXT, kind TEXT NOT NULL, created_at INTEGER NOT NULL)''');
          await db.execute('''CREATE TABLE action_items(
        id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
        text TEXT NOT NULL, owner TEXT, due_date TEXT, done INTEGER NOT NULL DEFAULT 0)''');
          await db.execute(
              'CREATE INDEX segments_meeting_sequence ON segments(meeting_id, sequence)');
          await _createLiveEvents(db);
        },
        onUpgrade: (db, oldVersion, newVersion) async {
          if (oldVersion < 2) await _createLiveEvents(db);
        });
    return _database!;
  }

  static Future<void> _createLiveEvents(DatabaseExecutor db) async {
    await db.execute('''CREATE TABLE IF NOT EXISTS live_transcript_events(
      id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
      start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, text TEXT NOT NULL,
      created_at INTEGER NOT NULL)''');
    await db.execute('CREATE INDEX IF NOT EXISTS live_events_meeting_time '
        'ON live_transcript_events(meeting_id, start_ms)');
  }

  Future<String> createMeeting(String profile) async {
    final now = DateTime.now();
    final id = now.microsecondsSinceEpoch.toString();
    final db = await database;
    await db.insert('meetings', {
      'id': id,
      'title': '會議 ${now.month}月${now.day}日',
      'created_at': now.millisecondsSinceEpoch,
      'state': MeetingState.recording.name,
      'selected_profile': profile,
    });
    return id;
  }

  Future<void> commitSegment(
      {required String id,
      required String meetingId,
      required int sequence,
      required String audioPath}) async {
    final db = await database;
    await db.transaction((transaction) async {
      await transaction.insert(
          'segments',
          {
            'id': id,
            'meeting_id': meetingId,
            'sequence': sequence,
            'audio_path': audioPath,
            'state': 'audio_saved',
          },
          conflictAlgorithm: ConflictAlgorithm.replace);
      await transaction.update(
          'meetings', {'state': MeetingState.recording.name},
          where: 'id=?', whereArgs: [meetingId]);
    });
  }

  Future<void> saveRawTranscript(String segmentId, String rawText) async {
    final db = await database;
    await db.update('segments', {'raw_text': rawText, 'state': 'live_complete'},
        where: 'id=?', whereArgs: [segmentId]);
  }

  Future<void> appendLiveTranscript(
      String meetingId, int startMs, int endMs, String text) async {
    final db = await database;
    final id = '$meetingId-$startMs-$endMs-${text.hashCode}';
    await db.insert(
        'live_transcript_events',
        {
          'id': id,
          'meeting_id': meetingId,
          'start_ms': startMs,
          'end_ms': endMs,
          'text': text,
          'created_at': DateTime.now().millisecondsSinceEpoch,
        },
        conflictAlgorithm: ConflictAlgorithm.ignore);
  }

  Future<List<String>> liveTranscript(String meetingId) async {
    final rows = await (await database).query('live_transcript_events',
        columns: ['text'],
        where: 'meeting_id=?',
        whereArgs: [meetingId],
        orderBy: 'start_ms, created_at');
    return rows.map((row) => row['text'] as String).toList();
  }

  Future<void> saveQualityTranscript(
      String segmentId, String formalText) async {
    final db = await database;
    await db.update(
        'segments', {'cleaned_text': formalText, 'state': 'quality_complete'},
        where: 'id=?', whereArgs: [segmentId]);
  }

  Future<void> setMeetingState(String meetingId, MeetingState state) async {
    await (await database).update('meetings', {'state': state.name},
        where: 'id=?', whereArgs: [meetingId]);
  }

  Future<void> saveMeetingReport(String meetingId, MeetingReport report) async {
    final db = await database;
    await db.transaction((transaction) async {
      await transaction.delete('action_items',
          where: 'meeting_id=?', whereArgs: [meetingId]);
      for (var index = 0; index < report.actionItems.length; index++) {
        final item = report.actionItems[index];
        await transaction.insert('action_items', {
          'id': '$meetingId-$index',
          'meeting_id': meetingId,
          'text': item.text,
          'owner': item.owner,
          'due_date': item.dueDate,
          'done': item.done ? 1 : 0,
        });
      }
      await transaction.update(
          'meetings',
          {
            'summary_json': jsonEncode(report.toJson()),
            'state': MeetingState.complete.name,
          },
          where: 'id=?',
          whereArgs: [meetingId]);
    });
  }

  Future<MeetingReport?> meetingReport(String meetingId) async {
    final rows = await (await database).query('meetings',
        columns: ['summary_json'],
        where: 'id=?',
        whereArgs: [meetingId],
        limit: 1);
    if (rows.isEmpty || rows.single['summary_json'] == null) return null;
    final decoded = jsonDecode(rows.single['summary_json'] as String);
    return decoded is Map<String, dynamic>
        ? MeetingReport.fromJson(decoded)
        : null;
  }

  Future<List<ActionItem>> actionItems(String meetingId) async {
    final rows = await (await database).query('action_items',
        where: 'meeting_id=?', whereArgs: [meetingId], orderBy: 'id');
    return rows
        .map((row) => ActionItem(
            id: row['id'] as String,
            text: row['text'] as String,
            owner: row['owner'] as String?,
            dueDate: row['due_date'] as String?,
            done: (row['done'] as int) != 0))
        .toList();
  }

  Future<void> finalizeMeeting(String id, int durationSeconds) async {
    final db = await database;
    await db.update('meetings',
        {'state': MeetingState.saved.name, 'duration_seconds': durationSeconds},
        where: 'id=?', whereArgs: [id]);
  }

  Future<void> addMarker(String meetingId, String kind) async {
    final now = DateTime.now().microsecondsSinceEpoch.toString();
    final db = await database;
    await db.insert('markers', {
      'id': now,
      'meeting_id': meetingId,
      'kind': kind,
      'created_at': DateTime.now().millisecondsSinceEpoch,
    });
  }

  Future<List<Meeting>> recentMeetings() async {
    final rows = await (await database)
        .query('meetings', orderBy: 'created_at DESC', limit: 50);
    return rows
        .map((row) => Meeting(
              id: row['id'] as String,
              title: row['title'] as String,
              createdAt:
                  DateTime.fromMillisecondsSinceEpoch(row['created_at'] as int),
              state: MeetingState.values.byName(row['state'] as String),
              durationSeconds: row['duration_seconds'] as int,
            ))
        .toList();
  }

  Future<List<TranscriptSegment>> segments(String meetingId) async {
    final rows = await (await database).query('segments',
        where: 'meeting_id=?', whereArgs: [meetingId], orderBy: 'sequence');
    return rows
        .map((row) => TranscriptSegment(
              id: row['id'] as String,
              meetingId: meetingId,
              sequence: row['sequence'] as int,
              audioPath: row['audio_path'] as String,
              rawText: row['raw_text'] as String?,
              cleanedText: row['cleaned_text'] as String?,
              userText: row['user_text'] as String?,
              speakerId: row['speaker_id'] as String?,
              state: row['state'] as String,
            ))
        .toList();
  }

  Future<List<Meeting>> recoverableMeetings() async {
    final rows = await (await database).query('meetings',
        where: 'state=?', whereArgs: [MeetingState.recording.name]);
    return rows
        .map((row) => Meeting(
              id: row['id'] as String,
              title: row['title'] as String,
              createdAt:
                  DateTime.fromMillisecondsSinceEpoch(row['created_at'] as int),
              state: MeetingState.recording,
              durationSeconds: row['duration_seconds'] as int,
            ))
        .toList();
  }

  Future<void> repairTemporaryFiles() async {
    final support = await getApplicationSupportDirectory();
    final recordings = Directory(p.join(support.path, 'meetings'));
    if (!await recordings.exists()) return;
    final db = await database;
    await for (final entity in recordings.list(recursive: true)) {
      if (entity is File && entity.path.endsWith('.m4a.part')) {
        // MediaMuxer writes the MP4/M4A moov atom only when the segment closes.
        // A process-killed .part is therefore not a playable recording and must
        // never be presented as one. Preserve it as explicitly incomplete.
        var incomplete =
            '${entity.path.substring(0, entity.path.length - 5)}.incomplete';
        if (await File(incomplete).exists()) {
          incomplete = '$incomplete-${DateTime.now().microsecondsSinceEpoch}';
        }
        await entity.rename(incomplete);
        continue;
      }
      if (entity is! File || !entity.path.endsWith('.m4a')) continue;
      final match =
          RegExp(r'^segment_(\d+)\.m4a$').firstMatch(p.basename(entity.path));
      if (match == null) continue;
      final meetingId = p.basename(entity.parent.path);
      final meeting = await db.query('meetings',
          columns: ['id', 'state'],
          where: 'id=?',
          whereArgs: [meetingId],
          limit: 1);
      if (meeting.isEmpty ||
          meeting.single['state'] != MeetingState.recording.name) {
        continue;
      }
      final sequence = int.parse(match.group(1)!);
      final segmentId = '$meetingId-$sequence';
      final existing = await db.query('segments',
          columns: ['id'], where: 'id=?', whereArgs: [segmentId], limit: 1);
      if (existing.isNotEmpty) continue;
      await commitSegment(
          id: segmentId,
          meetingId: meetingId,
          sequence: sequence,
          audioPath: entity.path);
    }
  }
}
