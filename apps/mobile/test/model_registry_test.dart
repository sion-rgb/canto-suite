import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:http/http.dart' as http;
import 'package:canto_meet/core/model_download.dart';
import 'package:canto_meet/core/model_registry.dart';

void main() {
  late Directory root;
  late MobileModelRegistry registry;
  final bytes = utf8.encode('deterministic model fixture, not inference');
  MobileModel model(String id, List<String> roles) => MobileModel({
        'id': id,
        'revision': 'revision',
        'displayName': id,
        'backend': 'fixture',
        'role': roles,
        'files': [
          {
            'path': roles.contains('MEETING_LLM') ? 'model.gguf' : 'model.onnx',
            'size': bytes.length,
            'sha256': sha256.convert(bytes).toString(),
            'sources': [
              {'label': 'primary', 'url': 'https://first.invalid/model'},
              {'label': 'fallback', 'url': 'https://second.invalid/model'}
            ]
          }
        ]
      });
  setUp(() async {
    root = await Directory.systemTemp.createTemp('canto-registry-test-');
    registry = MobileModelRegistry(root, [
      model(senseVoiceModelId, ['LIVE_ASR', 'QUALITY_ASR']),
      model(qwenAsrId, ['LIVE_ASR', 'QUALITY_ASR']),
      model(meetingLlmModelId, ['MEETING_LLM']),
      model(qwenQ8Id, ['MEETING_LLM'])
    ]);
    await registry.initialize();
    for (final model in registry.models) {
      await registry.install(model,
          client: MockClient((_) async => http.Response.bytes(bytes, 200)));
    }
  });
  tearDown(() async {
    await root.delete(recursive: true);
  });
  test(
      'independent roles persist real IDs and presets expose different bundles',
      () async {
    await registry.setActive('QUALITY_ASR', qwenAsrId);
    expect(registry.selected('LIVE_ASR').id, senseVoiceModelId);
    await registry.setActive('MEETING_LLM', qwenQ8Id);
    final reloaded = MobileModelRegistry(root, registry.models);
    await reloaded.initialize();
    expect(reloaded.selected('QUALITY_ASR').id, qwenAsrId);
    expect(reloaded.selected('MEETING_LLM').id, qwenQ8Id);
    final snapshots = <String>{};
    for (final name in mobilePresets.keys) {
      await registry.applyPreset(name);
      snapshots.add(jsonEncode(registry.selections));
    }
    expect(snapshots.length, 3);
    await expectLater(registry.setActive('LIVE_ASR', qwenQ8Id),
        throwsA(isA<ModelSelectionException>()));
  });
  test('selected and native-in-use models cannot be removed', () async {
    expect(registry.selectedRoles(senseVoiceModelId),
        ['LIVE_ASR', 'QUALITY_ASR']);
    await expectLater(registry.uninstall(registry.byId(senseVoiceModelId)),
        throwsA(predicate((error) => error is ModelSelectionException &&
            error.message.contains('LIVE_ASR／QUALITY_ASR'))));
    final lease = await registry.acquire('LIVE_ASR');
    await registry.setActive('LIVE_ASR', qwenAsrId);
    await registry.setActive('QUALITY_ASR', qwenAsrId);
    await expectLater(registry.uninstall(lease.model),
        throwsA(isA<ModelSelectionException>()));
    await expectLater(registry.install(lease.model, redownload: true),
        throwsA(isA<ModelSelectionException>()));
    expect(await registry.installed(lease.model), isTrue);
    lease.release();
    await registry.uninstall(lease.model);
    expect(await registry.installed(lease.model), isFalse);
  });
  test(
      'non-active ASR and LLM uninstall reclaims exact storage and removes metadata',
      () async {
    for (final id in [qwenAsrId, qwenQ8Id]) {
      final model = registry.byId(id);
      final before = await registry.storageBytes();
      final occupied = await registry.storageBytes(model);
      expect(await registry.uninstall(model), occupied);
      expect(before - await registry.storageBytes(), occupied);
      expect(await Directory('${root.path}/$id').exists(), isFalse);
      expect(await registry.installed(model), isFalse);
    }
  });
  test('delete failure restores files and registry state', () async {
    final model = registry.byId(qwenAsrId);
    await expectLater(
        registry.uninstall(model, deleteDirectory: (_) async {
          throw const FileSystemException('injected failure');
        }),
        throwsA(isA<FileSystemException>()));
    expect(await registry.installed(model), isTrue);
    expect(registry.selected('LIVE_ASR').id, senseVoiceModelId);
  });
  test('selection write failure preserves committed role IDs', () async {
    final before = await File('${root.path}/selections.json').readAsString();
    await Directory('${root.path}/selections.json.part').create();
    await expectLater(registry.setActive('QUALITY_ASR', qwenAsrId),
        throwsA(isA<FileSystemException>()));
    expect(await File('${root.path}/selections.json').readAsString(), before);
    expect(registry.selected('QUALITY_ASR').id, senseVoiceModelId);
  });
  test(
      'failed redownload preserves installed bytes; corrupt model cannot be activated',
      () async {
    final model = registry.byId(qwenAsrId);
    await expectLater(
        registry.install(model,
            redownload: true,
            client: MockClient((_) async =>
                throw const SocketException('injected DNS failure'))),
        throwsA(isA<ModelInstallException>()));
    expect(await registry.installed(model), isTrue);
    await File(
            '${registry.directory(model).path}/${model.files.first.relativePath}')
        .writeAsString('corrupt');
    await expectLater(registry.setActive('QUALITY_ASR', model.id),
        throwsA(isA<ModelSelectionException>()));
    expect(registry.selected('QUALITY_ASR').id, senseVoiceModelId);
  });
  test('catalog matches visible preset IDs, real names and backend roles',
      () async {
    final catalog = jsonDecode(
        await File('../../shared/model-catalog/catalog.v1.json')
            .readAsString()) as Map;
    expect(catalog['androidPresets'], mobilePresets);
    final models = (catalog['models'] as List)
        .where((model) => model['enabled'] == true)
        .toList();
    for (final preset in mobilePresets.values) {
      for (final role in preset.entries) {
        final model = models.singleWhere((model) => model['id'] == role.value);
        expect(model['role'], contains(role.key));
        expect(
            model['displayName'],
            isNot(anyOf(
                'High', 'Standard', 'Low', 'Local Meeting Intelligence')));
      }
    }
  });
}
