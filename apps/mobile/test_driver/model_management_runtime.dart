// Debug-only alternate entry point. Never used by the production APK.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'package:canto_core/canto_core.dart';
import 'package:flutter/material.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:canto_meet/core/model_registry.dart';
import 'package:canto_meet/core/model_download.dart';
import 'package:canto_meet/core/local_meeting_llm.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const MaterialApp(
      home: Scaffold(
          body: Center(
              child: Text(
                  'Model management native QA · see qa-model-management/evidence.json')))));
  final support = await getApplicationSupportDirectory();
  final root = Directory(p.join(support.path, 'qa-model-management'));
  await root.create(recursive: true);
  final evidence = <Map<String, dynamic>>[];
  Future<void> report(Map<String, dynamic> item) async {
    evidence.add({...item, 'time': DateTime.now().toUtc().toIso8601String()});
    final part = File(p.join(root.path, 'evidence.json.part'));
    await part.writeAsString(jsonEncode(evidence), flush: true);
    await part.rename(p.join(root.path, 'evidence.json'));
  }

  try {
    await report({'phase': 'waiting-for-pinned-staging-files'});
    while (!await File(p.join(root.path, 'seed-ready')).exists()) {
      await Future<void>.delayed(const Duration(seconds: 1));
    }
    final catalog = await MobileModelRegistry.readCatalog();
    final models = (catalog['models'] as List)
        .where((raw) =>
            raw['enabled'] == true &&
            (raw['platform'] as List).contains('android-arm64') &&
            (raw['role'] as List).any(mobileRoles.contains))
        .map((raw) => MobileModel(Map<String, dynamic>.from(raw as Map)))
        .toList();
    final registry = MobileModelRegistry(root, models);
    await registry.initialize();
    for (final model in models) {
      // Installer verifies pinned staged files and performs the normal atomic install.
      await registry.install(model);
      if (!await registry.installed(model)) {
        throw StateError('install not verified: ${model.id}');
      }
      await report({
        'phase': 'installed',
        'modelId': model.id,
        'bytes': await registry.storageBytes(model)
      });
    }
    final wav = await File(p.join(root.path, 'cantonese.wav')).readAsBytes();
    final data = ByteData.sublistView(wav);
    var offset = 12;
    while (ascii.decode(wav.sublist(offset, offset + 4)) != 'data') {
      offset += 8 + data.getUint32(offset + 4, Endian.little);
    }
    final count =
        (data.getUint32(offset + 4, Endian.little) ~/ 2).clamp(0, 16000 * 4);
    final pcm = Int16List(count);
    for (var index = 0; index < count; index++) {
      pcm[index] = data.getInt16(offset + 8 + index * 2, Endian.little);
    }
    for (final choice in [
      ('LIVE_ASR', senseVoiceModelId),
      ('QUALITY_ASR', qwenAsrId),
      ('LIVE_ASR', qwenAsrId),
      ('QUALITY_ASR', senseVoiceModelId)
    ]) {
      await registry.setActive(choice.$1, choice.$2);
      final lease = await registry.acquire(choice.$1);
      CantoCoreWorker? worker;
      StreamSubscription<TranscriptResult>? subscription;
      Timer? timer;
      try {
        worker = await CantoCoreWorker.start();
        await worker.loadModel(lease.nativePath);
        await lease.loaded();
        await report({
          'phase': 'native-loaded',
          'role': choice.$1,
          'modelId': lease.model.id,
          'path': lease.nativePath
        });
        final done = Completer<String>();
        final text = <String>[];
        subscription = worker.results.listen((result) {
          if (result.kind == TranscriptKind.error && !done.isCompleted) {
            done.completeError(StateError(result.text));
          }
          if (result.text.trim().isNotEmpty) text.add(result.text);
          if (result.kind == TranscriptKind.finalResult && !done.isCompleted) {
            done.complete(text.join(' '));
          }
        });
        worker.push(pcm, endOfStream: true);
        timer = Timer.periodic(
            const Duration(milliseconds: 100), (_) => worker?.poll());
        final textResult =
            await done.future.timeout(const Duration(minutes: 10));
        if (textResult.trim().isEmpty) throw StateError('no real ASR text');
        await report({
          'phase': 'native-asr-pass',
          'role': choice.$1,
          'modelId': lease.model.id,
          'text': textResult
        });
      } finally {
        timer?.cancel();
        await subscription?.cancel();
        await worker?.dispose();
        lease.release();
      }
    }
    for (final id in [meetingLlmModelId, qwenQ8Id]) {
      await registry.setActive('MEETING_LLM', id);
      final lease = await registry.acquire('MEETING_LLM');
      LocalLlmWorker? worker;
      try {
        worker = await LocalLlmWorker.start(lease.nativePath, threads: 2);
        await lease.loaded();
        await report({
          'phase': 'native-loaded',
          'role': 'MEETING_LLM',
          'modelId': id,
          'path': lease.nativePath
        });
        final text = await worker
            .generate('請回答：香港。/no_think', maxTokens: 12)
            .timeout(const Duration(minutes: 10));
        if (text.trim().isEmpty) throw StateError('empty LLM response');
        await report({'phase': 'native-llm-pass', 'modelId': id, 'text': text});
      } finally {
        await worker?.dispose();
        lease.release();
      }
    }
    await registry.setActive('LIVE_ASR', senseVoiceModelId);
    await registry.setActive('QUALITY_ASR', senseVoiceModelId);
    await registry.setActive('MEETING_LLM', meetingLlmModelId);
    for (final id in [qwenAsrId, qwenQ8Id]) {
      final model = registry.byId(id);
      final before = await registry.storageBytes();
      final reclaimed = await registry.uninstall(model);
      final after = await registry.storageBytes();
      if (before - after != reclaimed ||
          await Directory(p.join(root.path, id)).exists() ||
          await registry.installed(model)) {
        throw StateError('uninstall did not reclaim exact bytes: $id');
      }
      await report({
        'phase': 'uninstall-pass',
        'modelId': id,
        'beforeBytes': before,
        'afterBytes': after,
        'reclaimedBytes': reclaimed
      });
    }
    final reloaded = MobileModelRegistry(root, models);
    await reloaded.initialize();
    if (jsonEncode(reloaded.selections) != jsonEncode(registry.selections) ||
        !await reloaded.ready()) {
      throw StateError('role persistence/installed state mismatch');
    }
    await report({
      'phase': 'EMULATOR PASS',
      'roles': reloaded.selections,
      'physicalDevice': 'NOT TESTED'
    });
  } catch (error, stack) {
    await report({
      'phase': 'FAIL',
      'error': error.toString(),
      'stack': stack.toString()
    });
  }
}
