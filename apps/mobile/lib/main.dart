import 'dart:async';
import 'dart:io';
import 'dart:typed_data';
import 'package:canto_core/canto_core.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:http/http.dart' as http;
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'core/meeting_store.dart';
import 'core/model_download.dart';
import 'core/models.dart';
import 'core/local_meeting_llm.dart';
import 'core/quality_transcription.dart';
import 'core/segmented_recorder.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final store = MeetingStore();
  await store.repairTemporaryFiles();
  final preferences = await SharedPreferences.getInstance();
  runApp(CantoMeetApp(store: store, preferences: preferences));
}

class CantoMeetApp extends StatelessWidget {
  const CantoMeetApp(
      {super.key, required this.store, required this.preferences});
  final MeetingStore store;
  final SharedPreferences preferences;

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'CantoMeet',
        debugShowCheckedModeBanner: false,
        themeMode: ThemeMode.system,
        theme: _theme(Brightness.light),
        darkTheme: _theme(Brightness.dark),
        home: preferences.getString('quality_profile') == null ||
                !File(preferences.getString('asr_model_path') ?? '')
                    .existsSync() ||
                !File(preferences.getString('meeting_llm_path') ?? '')
                    .existsSync()
            ? QualitySetup(store: store, preferences: preferences)
            : HomeScreen(
                store: store,
                profile: preferences.getString('quality_profile')!,
                preferences: preferences),
      );

  ThemeData _theme(Brightness brightness) {
    final dark = brightness == Brightness.dark;
    return ThemeData(
      brightness: brightness,
      useMaterial3: true,
      colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xff176b4d), brightness: brightness),
      scaffoldBackgroundColor:
          dark ? const Color(0xff101512) : const Color(0xfff3f5f3),
      cardTheme: CardThemeData(
          elevation: 0,
          shape:
              RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
          margin: EdgeInsets.zero),
      appBarTheme: const AppBarTheme(centerTitle: false, elevation: 0),
    );
  }
}

class QualitySetup extends StatefulWidget {
  const QualitySetup(
      {super.key, required this.store, required this.preferences});
  final MeetingStore store;
  final SharedPreferences preferences;
  @override
  State<QualitySetup> createState() => _QualitySetupState();
}

class _QualitySetupState extends State<QualitySetup> {
  static const MethodChannel _hardware =
      MethodChannel('hk.canto.canto_meet/audio_control');
  String selected = 'standard';
  String outputScript = 'traditional';
  String hardwareDescription = '正在偵測裝置…';
  bool downloading = false;
  double progress = 0;
  String? installError;

  @override
  void initState() {
    super.initState();
    unawaited(_detectHardware());
  }

  Future<void> _detectHardware() async {
    try {
      final raw =
          await _hardware.invokeMapMethod<String, dynamic>('hardwareProfile');
      if (raw == null || !mounted) return;
      setState(() {
        selected = raw['recommendation']?.toString() ?? 'standard';
        hardwareDescription =
            '${raw['processors']} 核心 · 裝置記憶體 ${raw['memoryMiB']} MiB';
      });
    } catch (_) {
      if (mounted) setState(() => hardwareDescription = '未能讀取硬件資料 · 建議標準');
    }
  }

  Future<void> _installAndContinue() async {
    setState(() {
      downloading = true;
      installError = null;
    });
    final client = http.Client();
    try {
      final support = await getApplicationSupportDirectory();
      final root = Directory(p.join(support.path, 'models'));
      final installer = ModelInstaller(root, client);
      final asrBytes =
          senseVoiceModelFiles.fold<int>(0, (sum, file) => sum + file.size);
      final llmBytes =
          meetingLlmModelFiles.fold<int>(0, (sum, file) => sum + file.size);
      final allBytes = asrBytes + llmBytes;
      await installer.install(
          senseVoiceModelId, senseVoiceModelVersion, senseVoiceModelFiles,
          onProgress: (received, total) {
        if (mounted) setState(() => progress = received / allBytes);
      });
      await installer.install(
          meetingLlmModelId, meetingLlmModelVersion, meetingLlmModelFiles,
          onProgress: (received, total) {
        if (mounted) {
          setState(() => progress = (asrBytes + received) / allBytes);
        }
      });
      final modelPath =
          p.join(root.path, senseVoiceModelId, senseVoiceModelVersion);
      final llmPath = p.join(root.path, meetingLlmModelId,
          meetingLlmModelVersion, meetingLlmFileName);
      await widget.preferences.setString('quality_profile', selected);
      await widget.preferences.setString('output_script', outputScript);
      await widget.preferences.setString('asr_model_path', modelPath);
      await widget.preferences.setString('meeting_llm_path', llmPath);
      if (!mounted) return;
      await Navigator.of(context).pushReplacement(MaterialPageRoute(
          builder: (_) => HomeScreen(
              store: widget.store,
              profile: selected,
              preferences: widget.preferences)));
    } catch (error) {
      if (mounted) {
        setState(() {
          installError = error.toString();
          downloading = false;
        });
      }
    } finally {
      client.close();
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        body: SafeArea(
            child: Padding(
          padding: const EdgeInsets.all(24),
          child: ListView(children: [
            const SizedBox(height: 16),
            Icon(Icons.graphic_eq_rounded,
                size: 54, color: Theme.of(context).colorScheme.primary),
            const SizedBox(height: 20),
            Text('選擇 AI 品質',
                textAlign: TextAlign.center,
                style: Theme.of(context)
                    .textTheme
                    .headlineMedium
                    ?.copyWith(fontWeight: FontWeight.w700)),
            const SizedBox(height: 8),
            const Text('模型下載後，錄音、逐字稿同會議摘要都會留喺你部電話處理。',
                textAlign: TextAlign.center),
            const SizedBox(height: 6),
            Text(hardwareDescription,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 8),
            const Text('私隱：網絡只用於下載模型，不會上載錄音、逐字稿或摘要。',
                textAlign: TextAlign.center, style: TextStyle(fontSize: 12)),
            const SizedBox(height: 28),
            RadioGroup<String>(
              groupValue: selected,
              onChanged: (value) => setState(() => selected = value!),
              child: Column(children: [
                _profile('light', '輕量', '適合入門裝置及較低運算負載',
                    Icons.battery_saver_outlined),
                _profile('standard', '標準', '速度同準確度平衡', Icons.balance_outlined,
                    recommended: true),
                _profile(
                    'high', '高品質', '適合較高規格裝置', Icons.auto_awesome_outlined),
              ]),
            ),
            const SizedBox(height: 8),
            DropdownButtonFormField<String>(
              initialValue: outputScript,
              decoration: const InputDecoration(
                  labelText: '輸出字體', border: OutlineInputBorder()),
              items: const [
                DropdownMenuItem(
                    value: 'traditional', child: Text('繁體中文（香港，預設）')),
                DropdownMenuItem(value: 'simplified', child: Text('簡體中文')),
              ],
              onChanged: downloading
                  ? null
                  : (value) => setState(() => outputScript = value!),
            ),
            const SizedBox(height: 24),
            FilledButton(
              onPressed: downloading ? null : _installAndContinue,
              style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(54)),
              child: Text(downloading
                  ? '下載模型 ${(progress * 100).toStringAsFixed(0)}%'
                  : installError == null
                      ? '下載模型並繼續'
                      : '重試下載'),
            ),
            if (downloading) ...[
              const SizedBox(height: 10),
              LinearProgressIndicator(value: progress),
            ],
            if (installError != null) ...[
              const SizedBox(height: 10),
              Text('模型安裝失敗：$installError',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: Theme.of(context).colorScheme.error)),
            ],
            const SizedBox(height: 14),
          ]),
        )),
      );

  Widget _profile(String value, String title, String subtitle, IconData icon,
          {bool recommended = false}) =>
      Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Card(
          clipBehavior: Clip.antiAlias,
          child: RadioListTile<String>(
            value: value,
            title: Row(children: [
              Text(title, style: const TextStyle(fontWeight: FontWeight.w700)),
              if (recommended) ...[
                const SizedBox(width: 8),
                Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                    decoration: BoxDecoration(
                        color: Theme.of(context).colorScheme.primaryContainer,
                        borderRadius: BorderRadius.circular(20)),
                    child: const Text('建議', style: TextStyle(fontSize: 12))),
              ]
            ]),
            subtitle: Text(subtitle),
            secondary: Icon(icon),
            contentPadding:
                const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
          ),
        ),
      );
}

class OutputSettingsScreen extends StatefulWidget {
  const OutputSettingsScreen({super.key, required this.preferences});
  final SharedPreferences preferences;

  @override
  State<OutputSettingsScreen> createState() => _OutputSettingsScreenState();
}

class _OutputSettingsScreenState extends State<OutputSettingsScreen> {
  late String outputScript =
      widget.preferences.getString('output_script') ?? 'traditional';

  Future<void> _save(String value) async {
    await widget.preferences.setString('output_script', value);
    if (mounted) setState(() => outputScript = value);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('輸出設定')),
        body: ListView(padding: const EdgeInsets.all(20), children: [
          Text('中文輸出字體',
              style: Theme.of(context)
                  .textTheme
                  .titleLarge
                  ?.copyWith(fontWeight: FontWeight.w700)),
          const SizedBox(height: 8),
          const Text('設定會套用到之後產生的正式逐字稿、摘要及待辦。'),
          const SizedBox(height: 16),
          RadioGroup<String>(
            groupValue: outputScript,
            onChanged: (value) => _save(value!),
            child: const Card(
              child: Column(children: [
                RadioListTile<String>(
                    value: 'traditional', title: Text('繁體中文（香港，預設）')),
                Divider(height: 1),
                RadioListTile<String>(value: 'simplified', title: Text('簡體中文')),
              ]),
            ),
          ),
          const SizedBox(height: 12),
          const Text('所有轉換均在裝置內完成，不會上載會議內容。'),
        ]),
      );
}

class HomeScreen extends StatefulWidget {
  const HomeScreen(
      {super.key,
      required this.store,
      required this.profile,
      required this.preferences});
  final MeetingStore store;
  final String profile;
  final SharedPreferences preferences;
  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  late Future<List<Meeting>> meetings;
  late Future<List<Meeting>> recoverable;
  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    meetings = widget.store.recentMeetings();
    recoverable = widget.store.recoverableMeetings();
  }

  Future<void> _start() async {
    final meetingId = await widget.store.createMeeting(widget.profile);
    if (!mounted) return;
    await Navigator.of(context).push(MaterialPageRoute(
        builder: (_) =>
            RecordingScreen(store: widget.store, meetingId: meetingId)));
    setState(_reload);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(
            title: const Text('CantoMeet',
                style: TextStyle(fontWeight: FontWeight.w700)),
            actions: [
              IconButton(
                  tooltip: '輸出設定',
                  onPressed: () => Navigator.push(
                      context,
                      MaterialPageRoute(
                          builder: (_) => OutputSettingsScreen(
                              preferences: widget.preferences))),
                  icon: const Icon(Icons.settings_outlined))
            ]),
        body: RefreshIndicator(
            onRefresh: () async => setState(_reload),
            child: ListView(padding: const EdgeInsets.all(20), children: [
              FutureBuilder<List<Meeting>>(
                  future: recoverable,
                  builder: (context, snapshot) {
                    if (snapshot.data case final meetings?
                        when meetings.isNotEmpty) {
                      return Padding(
                          padding: const EdgeInsets.only(bottom: 16),
                          child: Card(
                              color:
                                  Theme.of(context).colorScheme.errorContainer,
                              child: ListTile(
                                leading: const Icon(Icons.restore),
                                title: const Text('發現未完成會議'),
                                subtitle: const Text('錄音片段已保留，可以進行復原。'),
                                trailing: const Icon(Icons.chevron_right),
                                onTap: () => Navigator.push(
                                    context,
                                    MaterialPageRoute(
                                        builder: (_) => MeetingDetailScreen(
                                            store: widget.store,
                                            meeting: meetings.first))),
                              )));
                    }
                    return const SizedBox.shrink();
                  }),
              Card(
                  child: Padding(
                      padding: const EdgeInsets.all(24),
                      child: Column(children: [
                        Container(
                            width: 70,
                            height: 70,
                            decoration: BoxDecoration(
                                color: Theme.of(context)
                                    .colorScheme
                                    .primaryContainer,
                                shape: BoxShape.circle),
                            child: Icon(Icons.mic_rounded,
                                size: 34,
                                color: Theme.of(context).colorScheme.primary)),
                        const SizedBox(height: 18),
                        Text('準備好開會？',
                            style: Theme.of(context)
                                .textTheme
                                .titleLarge
                                ?.copyWith(fontWeight: FontWeight.w700)),
                        const SizedBox(height: 8),
                        const Text('廣東話錄音、逐字稿同重點整理都在本機完成。',
                            textAlign: TextAlign.center),
                        const SizedBox(height: 22),
                        FilledButton.icon(
                            onPressed: _start,
                            icon: const Icon(Icons.mic),
                            label: const Text('開始新會議'),
                            style: FilledButton.styleFrom(
                                minimumSize: const Size.fromHeight(56))),
                      ]))),
              const SizedBox(height: 28),
              Text('最近會議',
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.w700)),
              const SizedBox(height: 12),
              FutureBuilder<List<Meeting>>(
                  future: meetings,
                  builder: (context, snapshot) {
                    if (snapshot.connectionState != ConnectionState.done) {
                      return const Center(child: CircularProgressIndicator());
                    }
                    if (snapshot.data?.isEmpty ?? true) {
                      return const Padding(
                          padding: EdgeInsets.symmetric(vertical: 40),
                          child: Center(child: Text('未有會議記錄')));
                    }
                    return Column(
                        children: snapshot.data!
                            .map((meeting) => Padding(
                                padding: const EdgeInsets.only(bottom: 10),
                                child: Card(
                                    child: ListTile(
                                  contentPadding: const EdgeInsets.symmetric(
                                      horizontal: 18, vertical: 7),
                                  leading: const CircleAvatar(
                                      child: Icon(Icons.notes_rounded)),
                                  title: Text(meeting.title,
                                      style: const TextStyle(
                                          fontWeight: FontWeight.w600)),
                                  subtitle: Text(
                                      '${meeting.createdAt.month}月${meeting.createdAt.day}日 · ${_duration(meeting.durationSeconds)}'),
                                  trailing: const Icon(Icons.chevron_right),
                                  onTap: () => Navigator.push(
                                      context,
                                      MaterialPageRoute(
                                          builder: (_) => MeetingDetailScreen(
                                              store: widget.store,
                                              meeting: meeting))),
                                ))))
                            .toList());
                  }),
            ])),
      );
}

class RecordingScreen extends StatefulWidget {
  const RecordingScreen(
      {super.key, required this.store, required this.meetingId});
  final MeetingStore store;
  final String meetingId;
  @override
  State<RecordingScreen> createState() => _RecordingScreenState();
}

class _RecordingScreenState extends State<RecordingScreen> {
  late final SegmentedRecorder recorder = SegmentedRecorder(widget.store);
  Timer? timer;
  Timer? pollTimer;
  CantoCoreWorker? coreWorker;
  StreamSubscription<Int16List>? pcmSubscription;
  StreamSubscription<TranscriptResult>? resultSubscription;
  final List<TranscriptResult> transcriptResults = [];
  int seconds = 0;
  bool starting = true;
  String? error;
  @override
  void initState() {
    super.initState();
    unawaited(_start());
  }

  Future<void> _start() async {
    try {
      final preferences = await SharedPreferences.getInstance();
      final modelPath = preferences.getString('asr_model_path');
      if (modelPath == null) throw StateError('未安裝廣東話語音模型');
      coreWorker = await CantoCoreWorker.start();
      resultSubscription = coreWorker!.results.listen(_onTranscriptResult);
      coreWorker!.loadModel(modelPath);
      pcmSubscription = recorder.pcm16.listen(
        (samples) => coreWorker?.push(samples),
        onError: (Object exception) {
          if (mounted) setState(() => error = exception.toString());
        },
      );
      await recorder.start(widget.meetingId);
      timer = Timer.periodic(const Duration(seconds: 1), (_) {
        if (mounted) setState(() => seconds++);
      });
      pollTimer = Timer.periodic(
          const Duration(milliseconds: 250), (_) => coreWorker?.poll());
      setState(() => starting = false);
    } catch (exception) {
      setState(() {
        error = exception.toString();
        starting = false;
      });
    }
  }

  void _onTranscriptResult(TranscriptResult result) {
    if (result.kind == TranscriptKind.error) {
      if (mounted) setState(() => error = result.text);
      return;
    }
    if (result.text.trim().isEmpty) return;
    transcriptResults.add(result);
    if (result.kind != TranscriptKind.partial) {
      unawaited(widget.store.appendLiveTranscript(
          widget.meetingId, result.startMs, result.endMs, result.text.trim()));
    }
    if (mounted) setState(() {});
  }

  String get liveTranscript => transcriptResults
      .where((result) => result.kind != TranscriptKind.partial)
      .map((result) => result.text.trim())
      .where((text) => text.isNotEmpty)
      .join('\n');

  Future<void> _stop() async {
    timer?.cancel();
    await recorder.stop();
    coreWorker?.push(Int16List(0), endOfStream: true);
    for (var attempt = 0; attempt < 40; attempt++) {
      coreWorker?.poll();
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
    pollTimer?.cancel();
    final segments = await widget.store.segments(widget.meetingId);
    for (final segment in segments) {
      final startMs =
          segment.sequence * recorder.segmentDuration.inMilliseconds;
      final endMs = startMs + recorder.segmentDuration.inMilliseconds;
      final text = transcriptResults
          .where((result) => result.startMs < endMs && result.endMs >= startMs)
          .map((result) => result.text.trim())
          .where((text) => text.isNotEmpty)
          .join('\n');
      if (text.isNotEmpty) {
        await widget.store.saveRawTranscript(segment.id, text);
      }
    }
    await pcmSubscription?.cancel();
    await resultSubscription?.cancel();
    coreWorker?.dispose();
    await widget.store.finalizeMeeting(widget.meetingId, seconds);
    if (!mounted) return;
    final meeting = Meeting(
        id: widget.meetingId,
        title: '剛完成嘅會議',
        createdAt: DateTime.now(),
        state: MeetingState.saved,
        durationSeconds: seconds);
    await Navigator.of(context).pushReplacement(MaterialPageRoute(
        builder: (_) =>
            ProcessingScreen(store: widget.store, meeting: meeting)));
  }

  @override
  void dispose() {
    timer?.cancel();
    pollTimer?.cancel();
    unawaited(pcmSubscription?.cancel());
    unawaited(resultSubscription?.cancel());
    coreWorker?.dispose();
    unawaited(recorder.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => PopScope(
        canPop: error != null,
        child: Scaffold(
          appBar: AppBar(
              title: const Text('錄音中'),
              automaticallyImplyLeading: error != null),
          body: SafeArea(
              child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: Column(children: [
                    if (starting)
                      const Expanded(
                          child: Center(child: CircularProgressIndicator()))
                    else if (error != null)
                      Expanded(child: Center(child: Text(error!)))
                    else ...[
                      const SizedBox(height: 8),
                      Row(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Container(
                                width: 10,
                                height: 10,
                                decoration: const BoxDecoration(
                                    color: Color(0xffd73a3a),
                                    shape: BoxShape.circle)),
                            const SizedBox(width: 9),
                            const Text('正在錄音')
                          ]),
                      const SizedBox(height: 14),
                      Text(_duration(seconds),
                          style: const TextStyle(
                              fontSize: 54,
                              fontWeight: FontWeight.w300,
                              fontFeatures: [FontFeature.tabularFigures()])),
                      const SizedBox(height: 22),
                      Expanded(
                          child: Card(
                              child: Padding(
                                  padding: const EdgeInsets.all(18),
                                  child: Column(
                                      crossAxisAlignment:
                                          CrossAxisAlignment.stretch,
                                      children: [
                                        const Text('即時逐字稿',
                                            style: TextStyle(
                                                fontWeight: FontWeight.w700)),
                                        const Divider(height: 28),
                                        Expanded(
                                            child: SingleChildScrollView(
                                                reverse: true,
                                                child: Text(
                                                    liveTranscript.isEmpty
                                                        ? '正在聆聽廣東話…'
                                                        : liveTranscript,
                                                    style: TextStyle(
                                                        fontSize: liveTranscript
                                                                .isEmpty
                                                            ? null
                                                            : 17,
                                                        height: 1.55,
                                                        color: liveTranscript
                                                                .isEmpty
                                                            ? Theme.of(context)
                                                                .colorScheme
                                                                .onSurfaceVariant
                                                            : null)))),
                                      ])))),
                      const SizedBox(height: 14),
                      Row(children: [
                        for (final marker in [
                          ('重點', 'highlight', Icons.star_outline),
                          ('決定', 'decision', Icons.check_circle_outline),
                          ('跟進', 'follow_up', Icons.help_outline)
                        ])
                          Expanded(
                              child: Padding(
                                  padding:
                                      const EdgeInsets.symmetric(horizontal: 4),
                                  child: OutlinedButton.icon(
                                      onPressed: () => widget.store.addMarker(
                                          widget.meetingId, marker.$2),
                                      icon: Icon(marker.$3),
                                      label: Text(marker.$1),
                                      style: OutlinedButton.styleFrom(
                                          minimumSize:
                                              const Size.fromHeight(48))))),
                      ]),
                      const SizedBox(height: 14),
                      FilledButton.icon(
                          onPressed: _stop,
                          icon: const Icon(Icons.stop_rounded),
                          label: const Text('停止並儲存'),
                          style: FilledButton.styleFrom(
                              backgroundColor: const Color(0xffb3261e),
                              foregroundColor: Colors.white,
                              minimumSize: const Size.fromHeight(58))),
                    ],
                  ]))),
        ),
      );
}

class ProcessingScreen extends StatefulWidget {
  const ProcessingScreen(
      {super.key, required this.store, required this.meeting});
  final MeetingStore store;
  final Meeting meeting;
  @override
  State<ProcessingScreen> createState() => _ProcessingScreenState();
}

class _ProcessingScreenState extends State<ProcessingScreen> {
  bool qualityComplete = false;
  bool summaryComplete = false;
  int completedSegments = 0;
  int totalSegments = 0;
  String? processingError;

  @override
  void initState() {
    super.initState();
    unawaited(_process());
  }

  Future<void> _process() async {
    setState(() => processingError = null);
    LocalLlmWorker? llm;
    try {
      final preferences = await SharedPreferences.getInstance();
      final asrPath = preferences.getString('asr_model_path');
      final llmPath = preferences.getString('meeting_llm_path');
      if (asrPath == null || llmPath == null) {
        throw StateError('本機 AI 模型未完整安裝');
      }
      final simplified = preferences.getString('output_script') == 'simplified';
      await QualityTranscriber(widget.store)
          .runMeeting(widget.meeting.id, asrPath, simplified: simplified,
              onProgress: (completed, total) {
        if (mounted) {
          setState(() {
            completedSegments = completed;
            totalSegments = total;
          });
        }
      });
      if (mounted) {
        setState(() => qualityComplete = true);
      }
      await widget.store
          .setMeetingState(widget.meeting.id, MeetingState.summarizing);
      final segments = await widget.store.segments(widget.meeting.id);
      llm = await LocalLlmWorker.start(llmPath,
          threads: Platform.numberOfProcessors.clamp(1, 6));
      final report = await HierarchicalMeetingSummarizer(llm.generate)
          .summarize(segments, simplified: simplified);
      await widget.store.saveMeetingReport(widget.meeting.id, report);
      if (mounted) setState(() => summaryComplete = true);
    } catch (error) {
      await widget.store
          .setMeetingState(widget.meeting.id, MeetingState.failed);
      if (mounted) setState(() => processingError = error.toString());
    } finally {
      await llm?.dispose();
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('處理會議')),
        body: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const SizedBox(height: 30),
                  const Icon(Icons.task_alt,
                      size: 64, color: Color(0xff176b4d)),
                  const SizedBox(height: 18),
                  Text('錄音已安全儲存',
                      textAlign: TextAlign.center,
                      style: Theme.of(context)
                          .textTheme
                          .headlineSmall
                          ?.copyWith(fontWeight: FontWeight.w700)),
                  const SizedBox(height: 32),
                  const _ProcessingStep(
                      icon: Icons.check_circle, title: '錄音已保存', complete: true),
                  const _ProcessingStep(
                      icon: Icons.check_circle,
                      title: '片段資料已保存',
                      complete: true),
                  _ProcessingStep(
                      icon: Icons.download_outlined,
                      title: '高精度轉錄',
                      subtitle: qualityComplete
                          ? null
                          : '已處理 $completedSegments / $totalSegments 個片段',
                      complete: qualityComplete),
                  _ProcessingStep(
                      icon: Icons.auto_awesome_outlined,
                      title: '整理會議重點',
                      subtitle: summaryComplete ? null : '使用本機會議模型進行階層式整理',
                      complete: summaryComplete),
                  if (processingError != null) ...[
                    const SizedBox(height: 12),
                    Text('處理失敗：$processingError',
                        style: TextStyle(
                            color: Theme.of(context).colorScheme.error)),
                    const SizedBox(height: 8),
                    OutlinedButton(
                        onPressed: _process, child: const Text('重試處理')),
                  ],
                  const Spacer(),
                  FilledButton(
                      onPressed: () => Navigator.pushReplacement(
                          context,
                          MaterialPageRoute(
                              builder: (_) => MeetingDetailScreen(
                                  store: widget.store,
                                  meeting: widget.meeting))),
                      style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(54)),
                      child: Text(summaryComplete ? '查看會議' : '查看已保存內容')),
                ])),
      );
}

class _ProcessingStep extends StatelessWidget {
  const _ProcessingStep(
      {required this.icon,
      required this.title,
      this.subtitle,
      this.complete = false});
  final IconData icon;
  final String title;
  final String? subtitle;
  final bool complete;
  @override
  Widget build(BuildContext context) => ListTile(
      contentPadding: EdgeInsets.zero,
      leading: Icon(icon, color: complete ? const Color(0xff176b4d) : null),
      title: Text(title),
      subtitle: subtitle == null ? null : Text(subtitle!),
      trailing: complete ? const Text('完成') : null);
}

class MeetingDetailScreen extends StatelessWidget {
  const MeetingDetailScreen(
      {super.key, required this.store, required this.meeting});
  final MeetingStore store;
  final Meeting meeting;
  @override
  Widget build(BuildContext context) => DefaultTabController(
      length: 4,
      child: Scaffold(
        appBar: AppBar(
            title: Text(meeting.title),
            bottom: const TabBar(isScrollable: true, tabs: [
              Tab(text: '摘要'),
              Tab(text: '待辦'),
              Tab(text: '逐字稿'),
              Tab(text: '資訊')
            ])),
        body: TabBarView(children: [
          _summaryTab(context),
          _actionsTab(context),
          _transcriptTab(context),
          ListView(padding: const EdgeInsets.all(20), children: [
            Card(
                child: Column(children: [
              ListTile(
                  title: const Text('錄音長度'),
                  trailing: Text(_duration(meeting.durationSeconds))),
              ListTile(title: const Text('處理方式'), trailing: const Text('全程本機')),
              const ListTile(title: Text('網絡用途'), trailing: Text('只下載模型'))
            ]))
          ]),
        ]),
      ));

  Widget _summaryTab(BuildContext context) => FutureBuilder<MeetingReport?>(
      future: store.meetingReport(meeting.id),
      builder: (context, snapshot) {
        final report = snapshot.data;
        if (report == null) {
          return _empty(context, Icons.summarize_outlined, '未有摘要',
              '完成本機高精度轉錄後，會喺呢度整理主題、決定同跟進。');
        }
        Widget section(String title, List<String> values) => values.isEmpty
            ? const SizedBox.shrink()
            : Card(
                child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(title,
                              style:
                                  const TextStyle(fontWeight: FontWeight.w700)),
                          const SizedBox(height: 8),
                          for (final value in values)
                            Padding(
                                padding: const EdgeInsets.only(bottom: 5),
                                child: Text('• $value')),
                        ])));
        return ListView(padding: const EdgeInsets.all(18), children: [
          Card(
              child: Padding(
                  padding: const EdgeInsets.all(18),
                  child: Text(
                      report.summary.isEmpty ? '未有可確認摘要' : report.summary))),
          const SizedBox(height: 10),
          section('核心主題', report.topics),
          const SizedBox(height: 10),
          section('決定', report.decisions),
          const SizedBox(height: 10),
          section('跟進', report.followUps),
          const SizedBox(height: 10),
          section('未解問題', report.unresolvedQuestions),
          const SizedBox(height: 10),
          section('風險', report.risks),
        ]);
      });

  Widget _actionsTab(BuildContext context) => FutureBuilder<List<ActionItem>>(
      future: store.actionItems(meeting.id),
      builder: (context, snapshot) {
        final items = snapshot.data ?? [];
        if (items.isEmpty) {
          return _empty(context, Icons.checklist_rounded, '未有待辦事項',
              '未指定負責人或限期嘅資料會保持「未指定」。');
        }
        return ListView.builder(
            padding: const EdgeInsets.all(18),
            itemCount: items.length,
            itemBuilder: (context, index) {
              final item = items[index];
              return Card(
                  child: ListTile(
                      leading: const Icon(Icons.check_box_outline_blank),
                      title: Text(item.text),
                      subtitle: Text(
                          '負責人：${item.owner ?? '未指定'} · 限期：${item.dueDate ?? '未指定'}')));
            });
      });

  Widget _transcriptTab(BuildContext context) =>
      FutureBuilder<(List<TranscriptSegment>, List<String>)>(
          future: _loadTranscript(),
          builder: (context, snapshot) {
            final segments = snapshot.data?.$1 ?? [];
            final live = snapshot.data?.$2 ?? [];
            if (segments.isEmpty && live.isEmpty) {
              return _empty(
                  context, Icons.notes_rounded, '未有逐字稿', '已保存嘅錄音可以稍後重新處理。');
            }
            if (segments.isEmpty) {
              return ListView(padding: const EdgeInsets.all(18), children: [
                Card(
                    child: Padding(
                        padding: const EdgeInsets.all(16),
                        child: Text(live.join('\n'))))
              ]);
            }
            return ListView.builder(
                padding: const EdgeInsets.all(18),
                itemCount: segments.length,
                itemBuilder: (context, index) => Card(
                    child: Padding(
                        padding: const EdgeInsets.all(16),
                        child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text('片段 ${index + 1}',
                                  style: const TextStyle(
                                      fontWeight: FontWeight.w700)),
                              const SizedBox(height: 8),
                              Text(segments[index].displayText.isEmpty
                                  ? live.join('\n')
                                  : segments[index].displayText)
                            ]))));
          });

  Future<(List<TranscriptSegment>, List<String>)> _loadTranscript() async => (
        await store.segments(meeting.id),
        await store.liveTranscript(meeting.id),
      );

  Widget _empty(BuildContext context, IconData icon, String title,
          String description) =>
      Center(
          child: Padding(
              padding: const EdgeInsets.all(36),
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Icon(icon,
                    size: 50, color: Theme.of(context).colorScheme.outline),
                const SizedBox(height: 16),
                Text(title,
                    style: Theme.of(context)
                        .textTheme
                        .titleMedium
                        ?.copyWith(fontWeight: FontWeight.w700)),
                const SizedBox(height: 8),
                Text(description, textAlign: TextAlign.center)
              ])));
}

String _duration(int totalSeconds) {
  final hours = totalSeconds ~/ 3600;
  final minutes = totalSeconds % 3600 ~/ 60;
  final seconds = totalSeconds % 60;
  return hours > 0
      ? '${hours.toString().padLeft(2, '0')}:${minutes.toString().padLeft(2, '0')}:${seconds.toString().padLeft(2, '0')}'
      : '${minutes.toString().padLeft(2, '0')}:${seconds.toString().padLeft(2, '0')}';
}
