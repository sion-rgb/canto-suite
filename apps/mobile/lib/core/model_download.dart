import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:http/http.dart' as http;
import 'package:path/path.dart' as p;

const senseVoiceModelId = 'sensevoice-yue-int8-2024-07-17';
const senseVoiceModelVersion = '2365baeacb507f821a0c8120fcee3d484dba7a07';
const meetingLlmModelId = 'qwen3-0.6b-q4-k-m';
const meetingLlmModelVersion = '1208e45d782fe18602c5eaf10e5758d5b0f24c03';
const meetingLlmFileName = 'Qwen3-0.6B-Q4_K_M.gguf';

const _senseVoiceHf =
    'https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/$senseVoiceModelVersion';
const _senseVoiceMirror =
    'https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/$senseVoiceModelVersion';
const _qwenHf =
    'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/$meetingLlmModelVersion';
const _qwenMirror =
    'https://hf-mirror.com/Qwen/Qwen3-0.6B-GGUF/resolve/$meetingLlmModelVersion';

List<ModelSource> _sources(String primary, String fallback, String path) => [
      ModelSource(
          label: 'Hugging Face',
          url: Uri.parse('$primary/$path?download=true')),
      ModelSource(
          label: 'Hugging Face 備用鏡像',
          url: Uri.parse('$fallback/$path?download=true')),
    ];

final senseVoiceModelFiles = <ModelFile>[
  ModelFile(
      sources: _sources(_senseVoiceHf, _senseVoiceMirror, 'model.int8.onnx'),
      relativePath: 'model.int8.onnx',
      sha256:
          'c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51',
      size: 239233841),
  ModelFile(
      sources: _sources(_senseVoiceHf, _senseVoiceMirror, 'tokens.txt'),
      relativePath: 'tokens.txt',
      sha256:
          'f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc',
      size: 315894),
  ModelFile(
      sources: _sources(_senseVoiceHf, _senseVoiceMirror, 'LICENSE'),
      relativePath: 'LICENSE',
      sha256:
          '221c6df10b0931a5629adad671ea48fb7747e034c414b6d2bfa275bc3dd4ea17',
      size: 71),
];

final senseVoiceModelBundles = <ModelBundle>[
  ModelBundle(
      sources: [
        ModelSource(
            label: 'sherpa-onnx 官方 GitHub',
            url: Uri.parse(
                'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2'))
      ],
      fileName:
          'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2',
      sha256:
          '7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e',
      size: 163002883,
      extractRoot: 'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17'),
];

final meetingLlmModelFiles = <ModelFile>[
  ModelFile(
      sources: _sources(_qwenHf, _qwenMirror, meetingLlmFileName),
      relativePath: meetingLlmFileName,
      sha256:
          'b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e',
      size: 396704416),
  ModelFile(
      sources: _sources(_qwenHf, _qwenMirror, 'LICENSE'),
      relativePath: 'LICENSE',
      sha256:
          '5de36594c10839788a8c589443a8ef9d8b8d17c65a1b5807206ae037fc36c6bd',
      size: 11544),
];

class ModelSource {
  const ModelSource({required this.label, required this.url});

  final String label;
  final Uri url;
}

class ModelFile {
  const ModelFile(
      {required this.sources,
      required this.relativePath,
      required this.sha256,
      required this.size})
      : assert(sources.length > 0);

  final List<ModelSource> sources;
  final String relativePath;
  final String sha256;
  final int size;
}

class ModelBundle {
  const ModelBundle(
      {required this.sources,
      required this.fileName,
      required this.sha256,
      required this.size,
      required this.extractRoot})
      : assert(sources.length > 0);

  final List<ModelSource> sources;
  final String fileName;
  final String sha256;
  final int size;
  final String extractRoot;
}

class ModelInstallException implements Exception {
  const ModelInstallException(this.technicalDetails);

  final String technicalDetails;
  String get userMessage => '未能下載模型。請檢查網絡後重試；程式已自動嘗試備用來源。';

  @override
  String toString() => technicalDetails;
}

class ModelInstaller {
  const ModelInstaller(this.root, this.client,
      {this.maxAttemptsPerSource = 2,
      this.requestTimeout = const Duration(seconds: 25),
      this.streamIdleTimeout = const Duration(seconds: 45)});

  final Directory root;
  final http.Client client;
  final int maxAttemptsPerSource;
  final Duration requestTimeout;
  final Duration streamIdleTimeout;

  Future<void> install(String modelId, String version, List<ModelFile> files,
      {List<ModelBundle> bundles = const [],
      void Function(int, int)? onProgress,
      void Function(String)? onSourceChanged}) async {
    final installed = Directory(p.join(root.path, modelId, version));
    if (await _verifyAll(files, installed)) {
      final total = files.fold<int>(0, (value, file) => value + file.size);
      onProgress?.call(total, total);
      return;
    }

    final staging =
        Directory(p.join(root.path, '.staging', '$modelId-$version'));
    await staging.create(recursive: true);
    final failures = <String>[];

    for (final bundle in bundles) {
      try {
        await _installBundle(bundle, files, staging,
            onProgress: onProgress, onSourceChanged: onSourceChanged);
        if (await _verifyAll(files, staging)) break;
      } catch (error) {
        failures.add('Bundle ${bundle.fileName}: $error');
      }
    }

    var completed = 0;
    final total = files.fold<int>(0, (value, file) => value + file.size);
    for (final file in files) {
      final destination = File(p.join(staging.path, file.relativePath));
      await destination.parent.create(recursive: true);
      if (await verify(file, staging)) {
        completed += file.size;
        onProgress?.call(completed, total);
        continue;
      }
      if (await destination.exists()) await destination.delete();
      final part = File('${destination.path}.part');
      try {
        await _downloadVerified(
            sources: file.sources,
            part: part,
            size: file.size,
            expectedSha256: file.sha256,
            description: file.relativePath,
            onSourceChanged: onSourceChanged,
            onBytes: (received) =>
                onProgress?.call(completed + received, total));
        await part.rename(destination.path);
        completed += file.size;
      } catch (error) {
        failures.add('${file.relativePath}: $error');
        throw ModelInstallException(failures.join('\n'));
      }
    }

    final bundleCache = Directory(p.join(staging.path, '.bundles'));
    if (await bundleCache.exists()) await bundleCache.delete(recursive: true);
    final extractCache = Directory(p.join(staging.path, '.bundle-extract'));
    if (await extractCache.exists()) await extractCache.delete(recursive: true);

    await File(p.join(staging.path, 'install.json')).writeAsString(
        jsonEncode({
          'modelId': modelId,
          'version': version,
          'installedAt': DateTime.now().toUtc().toIso8601String()
        }),
        flush: true);
    await installed.parent.create(recursive: true);
    final backup = Directory('${installed.path}.previous');
    if (await backup.exists()) await backup.delete(recursive: true);
    if (await installed.exists()) await installed.rename(backup.path);
    try {
      await staging.rename(installed.path);
      if (await backup.exists()) await backup.delete(recursive: true);
    } catch (_) {
      if (!await installed.exists() && await backup.exists()) {
        await backup.rename(installed.path);
      }
      rethrow;
    }
    final pinPart = File(p.join(root.path, modelId, 'current.json.part'));
    await pinPart.writeAsString(jsonEncode({'version': version}), flush: true);
    final pin = File(p.join(root.path, modelId, 'current.json'));
    if (await pin.exists()) await pin.delete();
    await pinPart.rename(pin.path);
  }

  Future<void> _installBundle(
      ModelBundle bundle, List<ModelFile> files, Directory staging,
      {void Function(int, int)? onProgress,
      void Function(String)? onSourceChanged}) async {
    final cache = Directory(p.join(staging.path, '.bundles'));
    await cache.create(recursive: true);
    final archive = File(p.join(cache.path, bundle.fileName));
    if (!await _verifyArtifact(archive, bundle.size, bundle.sha256)) {
      if (await archive.exists()) await archive.delete();
      final part = File('${archive.path}.part');
      await _downloadVerified(
          sources: bundle.sources,
          part: part,
          size: bundle.size,
          expectedSha256: bundle.sha256,
          description: bundle.fileName,
          onSourceChanged: onSourceChanged,
          onBytes: (received) => onProgress?.call(received, bundle.size));
      await part.rename(archive.path);
    }

    final extract = Directory(p.join(staging.path, '.bundle-extract'));
    if (await extract.exists()) await extract.delete(recursive: true);
    await extract.create(recursive: true);
    await extractFileToDisk(archive.path, extract.path);
    final sourceRoot = Directory(p.join(extract.path, bundle.extractRoot));
    for (final file in files) {
      final source = File(p.join(sourceRoot.path, file.relativePath));
      if (!await _verifyArtifact(source, file.size, file.sha256)) {
        throw StateError(
            'Archive member identity mismatch: ${file.relativePath}');
      }
      final destination = File(p.join(staging.path, file.relativePath));
      await destination.parent.create(recursive: true);
      if (await destination.exists()) await destination.delete();
      await source.rename(destination.path);
    }
  }

  Future<void> _downloadVerified(
      {required List<ModelSource> sources,
      required File part,
      required int size,
      required String expectedSha256,
      required String description,
      void Function(int)? onBytes,
      void Function(String)? onSourceChanged}) async {
    final failures = <String>[];
    for (final source in sources) {
      onSourceChanged?.call(source.label);
      for (var attempt = 1; attempt <= maxAttemptsPerSource; attempt++) {
        try {
          var existing = await part.exists() ? await part.length() : 0;
          if (existing >= size) {
            if (existing == size &&
                await _verifyArtifact(part, size, expectedSha256)) {
              onBytes?.call(size);
              return;
            }
            await part.delete();
            existing = 0;
          }
          final request = http.Request('GET', source.url)
            ..headers['Range'] = 'bytes=$existing-';
          final response = await client.send(request).timeout(requestTimeout);
          if (response.statusCode != 200 && response.statusCode != 206) {
            throw HttpException('HTTP ${response.statusCode}', uri: source.url);
          }
          final contentRange = response.headers['content-range'];
          final canAppend = existing > 0 &&
              response.statusCode == 206 &&
              contentRange != null &&
              contentRange.startsWith('bytes $existing-');
          if (existing > 0 && response.statusCode == 206 && !canAppend) {
            await part.delete();
            throw const FormatException('Invalid Content-Range for resume');
          }
          final sink = part.openWrite(
              mode: canAppend ? FileMode.append : FileMode.write);
          var received = canAppend ? existing : 0;
          try {
            await for (final chunk
                in response.stream.timeout(streamIdleTimeout)) {
              sink.add(chunk);
              received += chunk.length;
              if (received > size) {
                throw const FormatException(
                    'Downloaded file exceeds pinned size');
              }
              onBytes?.call(received);
            }
            await sink.flush();
          } finally {
            await sink.close();
          }
          final actualSize = await part.length();
          if (actualSize != size) {
            throw StateError(
                'Incomplete $description ($actualSize of $size bytes)');
          }
          if (!await _verifyArtifact(part, size, expectedSha256)) {
            await part.delete();
            throw StateError('SHA-256 mismatch for $description');
          }
          return;
        } catch (error) {
          failures.add('${source.label} attempt $attempt: $error');
          if (attempt < maxAttemptsPerSource) {
            await Future<void>.delayed(Duration(milliseconds: 400 * attempt));
          }
        }
      }
    }
    throw ModelInstallException(failures.join('\n'));
  }

  Future<bool> _verifyAll(List<ModelFile> files, Directory directory) async {
    if (!await directory.exists()) return false;
    for (final file in files) {
      if (!await verify(file, directory)) return false;
    }
    return true;
  }

  Future<bool> _verifyArtifact(
      File file, int expectedSize, String expectedSha256) async {
    if (!await file.exists() || await file.length() != expectedSize) {
      return false;
    }
    return (await sha256.bind(file.openRead()).first)
            .toString()
            .toLowerCase() ==
        expectedSha256.toLowerCase();
  }

  Future<bool> verify(ModelFile file, Directory installed) => _verifyArtifact(
      File(p.join(installed.path, file.relativePath)), file.size, file.sha256);
}
