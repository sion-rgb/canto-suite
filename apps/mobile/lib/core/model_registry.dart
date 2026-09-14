import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:http/http.dart' as http;
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import 'model_download.dart';

const mobileRoles = ['LIVE_ASR', 'QUALITY_ASR', 'MEETING_LLM'];
const qwenAsrId = 'qwen3-asr-0.6b-int8-2026-03-25';
const qwenQ8Id = 'qwen3-0.6b-q8-0';
const mobilePresets = {
  'light': {
    'LIVE_ASR': senseVoiceModelId,
    'QUALITY_ASR': senseVoiceModelId,
    'MEETING_LLM': meetingLlmModelId
  },
  'standard': {
    'LIVE_ASR': senseVoiceModelId,
    'QUALITY_ASR': qwenAsrId,
    'MEETING_LLM': meetingLlmModelId
  },
  'high': {
    'LIVE_ASR': senseVoiceModelId,
    'QUALITY_ASR': qwenAsrId,
    'MEETING_LLM': qwenQ8Id
  },
};

class MobileModel {
  MobileModel(Map<String, dynamic> json)
      : id = json['id'] as String,
        name = json['displayName'] as String,
        revision = json['revision'] as String,
        backend = json['backend'] as String,
        roles = List<String>.from(json['role'] as List),
        files = (json['files'] as List)
            .map((raw) => ModelFile(
                relativePath: raw['path'] as String,
                size: raw['size'] as int,
                sha256: raw['sha256'] as String,
                sources: _sources(raw['sources'] as List)))
            .toList(),
        bundles = ((json['downloadBundles'] as List?) ?? [])
            .map((raw) => ModelBundle(
                fileName: raw['path'] as String,
                size: raw['size'] as int,
                sha256: raw['sha256'] as String,
                extractRoot: raw['extractRoot'] as String,
                sources: _sources(raw['sources'] as List)))
            .toList() {
    final component = RegExp(r'^[a-zA-Z0-9][a-zA-Z0-9._-]*$');
    if (!component.hasMatch(id) ||
        !component.hasMatch(revision) ||
        files.isEmpty) {
      throw const FormatException('不安全或空白模型項目');
    }
    for (final file in files) {
      if (p.isAbsolute(file.relativePath) ||
          file.relativePath.split(RegExp(r'[/\\]')).contains('..') ||
          !RegExp(r'^[a-f0-9]{64}$').hasMatch(file.sha256) ||
          file.size <= 0 ||
          file.sources.any((source) => source.url.scheme != 'https')) {
        throw const FormatException('模型路徑或校驗資料不正確');
      }
    }
  }
  final String id, name, revision, backend;
  final List<String> roles;
  final List<ModelFile> files;
  final List<ModelBundle> bundles;
  int get bytes => files.fold(0, (sum, file) => sum + file.size);
  static List<ModelSource> _sources(List raw) => raw
      .map((source) => ModelSource(
          label: source['label'] as String,
          url: Uri.parse(source['url'] as String)))
      .toList();
}

class ModelSelectionException implements Exception {
  const ModelSelectionException(this.message);
  final String message;
  @override
  String toString() => message;
}

class MobileModelLease {
  MobileModelLease._(this.registry, this.role, this.model);
  final MobileModelRegistry registry;
  final String role;
  final MobileModel model;
  bool _released = false;
  String get directory => registry.directory(model).path;
  String get nativePath => role == 'MEETING_LLM'
      ? p.join(
          directory,
          model.files
              .singleWhere((file) => file.relativePath.endsWith('.gguf'))
              .relativePath)
      : directory;
  // Called only AFTER a native load acknowledgement, never after a UI selection.
  Future<void> loaded() => registry.recordLoaded(this);
  void release() {
    if (_released) return;
    _released = true;
    registry._users[model.id] = (registry._users[model.id] ?? 1) - 1;
  }
}

class MobileModelRegistry {
  MobileModelRegistry(this.root, this.models);
  final Directory root;
  final List<MobileModel> models;
  Map<String, String> selections = {};
  String preset = 'light';
  final Map<String, int> _users = {};
  final Set<String> _mutating = {};
  Future<void> _selectionTail = Future.value();
  static Future<MobileModelRegistry>? _instance;

  static Future<MobileModelRegistry> open() =>
      _instance ??= _open().catchError((Object error) {
        _instance = null;
        throw error;
      });
  static Future<Map> readCatalog() async =>
      jsonDecode((await const MethodChannel('hk.canto.canto_meet/audio_control')
              .invokeMethod<String>('modelCatalog')) ??
          (throw StateError('模型目錄未能讀取'))) as Map;
  static Future<MobileModelRegistry> _open() async {
    final catalog = await readCatalog();
    final models = (catalog['models'] as List)
        .where((raw) =>
            raw['enabled'] == true &&
            (raw['platform'] as List).contains('android-arm64') &&
            (raw['role'] as List).any(mobileRoles.contains))
        .map((raw) => MobileModel(Map<String, dynamic>.from(raw as Map)))
        .toList();
    final support = await getApplicationSupportDirectory();
    final registry =
        MobileModelRegistry(Directory(p.join(support.path, 'models')), models);
    await registry.initialize();
    return registry;
  }

  Future<void> initialize() async {
    await root.create(recursive: true);
    final settings = File(p.join(root.path, 'selections.json'));
    if (await settings.exists()) {
      final data = jsonDecode(await settings.readAsString()) as Map;
      selections = Map<String, String>.from(data['roles'] as Map);
      preset = data['preset'] as String? ?? 'custom';
    } else {
      // Existing releases installed exactly these IDs. Preserve that bundle on upgrade.
      selections = Map.of(mobilePresets['light']!);
    }
    for (final role in mobileRoles) {
      selected(role);
    }
    await _save(Map.of(selections), preset);
  }

  MobileModel byId(String id) => models.firstWhere((model) => model.id == id);
  MobileModel selected(String role) {
    final model = byId(selections[role] ?? '');
    if (!mobileRoles.contains(role) || !model.roles.contains(role)) {
      throw const ModelSelectionException('模型不支援呢個角色');
    }
    return model;
  }

  Directory directory(MobileModel model) =>
      Directory(p.join(root.path, model.id, model.revision));
  bool isSelected(String id) => selections.values.contains(id);
  List<String> selectedRoles(String id) => mobileRoles
      .where((role) => selections[role] == id)
      .toList(growable: false);
  bool inUse(String id) => (_users[id] ?? 0) > 0;

  Future<bool> installed(MobileModel model) async {
    final client = http.Client();
    try {
      final installer = ModelInstaller(root, client);
      for (final file in model.files) {
        if (!await installer.verify(file, directory(model))) return false;
      }
      return true;
    } finally {
      client.close();
    }
  }

  Future<bool> ready() async {
    for (final id in selections.values.toSet()) {
      if (!await installed(byId(id))) return false;
    }
    return true;
  }

  Future<int> storageBytes([MobileModel? model]) async {
    final folder =
        model == null ? root : Directory(p.join(root.path, model.id));
    if (!await folder.exists()) return 0;
    var bytes = 0;
    await for (final entry
        in folder.list(recursive: true, followLinks: false)) {
      if (entry is File) bytes += await entry.length();
    }
    return bytes;
  }

  Future<T> _change<T>(Future<T> Function() action) {
    final next = _selectionTail.then((_) => action());
    _selectionTail =
        next.then<void>((_) {}, onError: (Object _, StackTrace __) {});
    return next;
  }

  Future<void> setActive(String role, String id) => _change(() async {
        final model = byId(id);
        if (!mobileRoles.contains(role) || !model.roles.contains(role)) {
          throw const ModelSelectionException('模型不支援呢個角色');
        }
        if (_mutating.contains(id) || !await installed(model)) {
          throw const ModelSelectionException('請先下載並完整驗證呢個模型');
        }
        await _save({...selections, role: id}, 'custom');
      });
  Future<void> applyPreset(String name) => _change(() async {
        final roles = mobilePresets[name] ??
            (throw const ModelSelectionException('未知預設組合'));
        for (final entry in roles.entries) {
          if (!byId(entry.value).roles.contains(entry.key)) {
            throw const ModelSelectionException('預設模型不相容');
          }
        }
        // Selected-but-uninstalled is explicit in UI and acquire() disables the feature.
        await _save(Map.of(roles), name);
      });
  Future<void> _save(Map<String, String> next, String nextPreset) async {
    final destination = File(p.join(root.path, 'selections.json'));
    final part = File('${destination.path}.part');
    await part.writeAsString(
        jsonEncode({'version': 1, 'roles': next, 'preset': nextPreset}),
        flush: true);
    await part.rename(destination.path);
    selections = next;
    preset = nextPreset;
  }

  Future<MobileModelLease> acquire(String role) async {
    final model = selected(role);
    if (_mutating.contains(model.id)) {
      throw const ModelSelectionException('模型正在更新中');
    }
    _users[model.id] = (_users[model.id] ?? 0) + 1;
    final lease = MobileModelLease._(this, role, model);
    try {
      if (!await installed(model)) {
        throw ModelSelectionException('$role 尚未安裝或需修復，請開啟設定 → AI 模型');
      }
      return lease;
    } catch (_) {
      lease.release();
      rethrow;
    }
  }

  Future<void> recordLoaded(MobileModelLease lease) async {
    final destination = File(p.join(root.path, 'loaded-${lease.role}.json'));
    final part = File('${destination.path}.part');
    await part.writeAsString(
        jsonEncode({
          'role': lease.role,
          'modelId': lease.model.id,
          'revision': lease.model.revision,
          'backend': lease.model.backend,
          'nativePath': lease.nativePath,
          'nativeLoadAcknowledged': true,
          'loadedAt': DateTime.now().toUtc().toIso8601String()
        }),
        flush: true);
    await part.rename(destination.path);
  }

  void _beginMutation(String id) {
    if (inUse(id) || !_mutating.add(id)) {
      throw const ModelSelectionException('模型正在使用或更新中，請等工作完全停止後再試');
    }
  }

  Future<void> install(MobileModel model,
      {bool redownload = false,
      void Function(int, int)? onProgress,
      void Function(String)? onSourceChanged,
      http.Client? client}) async {
    _beginMutation(model.id);
    final downloadClient = client ?? http.Client();
    try {
      await ModelInstaller(root, downloadClient).install(
          model.id, model.revision, model.files,
          bundles: model.bundles,
          forceRedownload: redownload,
          onProgress: onProgress,
          onSourceChanged: onSourceChanged);
    } finally {
      if (client == null) downloadClient.close();
      _mutating.remove(model.id);
    }
  }

  Future<int> uninstall(MobileModel model,
          {Future<void> Function(Directory)? deleteDirectory}) =>
      _change(() async {
        final activeRoles = selectedRoles(model.id);
        if (activeRoles.isNotEmpty) {
          throw ModelSelectionException(
              '模型正在供 ${activeRoles.join('／')} 使用。請先為該角色切換至另一個已安裝模型，再卸載');
        }
        _beginMutation(model.id);
        try {
          final folder = Directory(p.join(root.path, model.id));
          if (!await folder.exists()) return 0;
          final bytes = await storageBytes(model);
          final trash = Directory(p.join(root.path, '.trash',
              '${model.id}-${DateTime.now().microsecondsSinceEpoch}'));
          await trash.parent.create(recursive: true);
          await folder.rename(trash.path);
          try {
            await (deleteDirectory ??
                (directory) async {
                  await directory.delete(recursive: true);
                })(trash);
          } catch (_) {
            if (await trash.exists() && !await folder.exists()) {
              await trash.rename(folder.path);
            }
            rethrow;
          }
          return bytes;
        } finally {
          _mutating.remove(model.id);
        }
      });
}
