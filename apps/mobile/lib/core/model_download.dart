import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:http/http.dart' as http;
import 'package:path/path.dart' as p;

const senseVoiceModelId = 'sensevoice-yue-int8-2024-07-17';
const senseVoiceModelVersion = '2365baeacb507f821a0c8120fcee3d484dba7a07';
const meetingLlmModelId = 'qwen3-0.6b-q4-k-m';
const meetingLlmModelVersion = '1208e45d782fe18602c5eaf10e5758d5b0f24c03';
const meetingLlmFileName = 'Qwen3-0.6B-Q4_K_M.gguf';

final senseVoiceModelFiles = <ModelFile>[
  ModelFile(
      url: Uri.parse(
          'https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/$senseVoiceModelVersion/model.int8.onnx?download=true'),
      relativePath: 'model.int8.onnx',
      sha256:
          'c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51',
      size: 239233841),
  ModelFile(
      url: Uri.parse(
          'https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/$senseVoiceModelVersion/tokens.txt?download=true'),
      relativePath: 'tokens.txt',
      sha256:
          'f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc',
      size: 315894),
  ModelFile(
      url: Uri.parse(
          'https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/$senseVoiceModelVersion/LICENSE?download=true'),
      relativePath: 'LICENSE',
      sha256:
          '221c6df10b0931a5629adad671ea48fb7747e034c414b6d2bfa275bc3dd4ea17',
      size: 71),
];

final meetingLlmModelFiles = <ModelFile>[
  ModelFile(
      url: Uri.parse(
          'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/$meetingLlmModelVersion/$meetingLlmFileName?download=true'),
      relativePath: meetingLlmFileName,
      sha256:
          'b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e',
      size: 396704416),
  ModelFile(
      url: Uri.parse(
          'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/$meetingLlmModelVersion/LICENSE?download=true'),
      relativePath: 'LICENSE',
      sha256:
          '5de36594c10839788a8c589443a8ef9d8b8d17c65a1b5807206ae037fc36c6bd',
      size: 11544),
];

class ModelFile {
  const ModelFile(
      {required this.url,
      required this.relativePath,
      required this.sha256,
      required this.size});
  final Uri url;
  final String relativePath;
  final String sha256;
  final int size;
}

class ModelInstaller {
  const ModelInstaller(this.root, this.client);
  final Directory root;
  final http.Client client;

  Future<void> install(String modelId, String version, List<ModelFile> files,
      {void Function(int, int)? onProgress}) async {
    final installed = Directory(p.join(root.path, modelId, version));
    if (await installed.exists()) {
      var valid = true;
      for (final file in files) {
        if (!await verify(file, installed)) {
          valid = false;
          break;
        }
      }
      if (valid) {
        final total = files.fold<int>(0, (value, file) => value + file.size);
        onProgress?.call(total, total);
        return;
      }
    }
    final staging =
        Directory(p.join(root.path, '.staging', '$modelId-$version'));
    await staging.create(recursive: true);
    var completed = 0;
    final total = files.fold<int>(0, (value, file) => value + file.size);
    for (final file in files) {
      final destination = File(p.join(staging.path, file.relativePath));
      await destination.parent.create(recursive: true);
      if (await destination.exists()) {
        if (await verify(file, staging)) {
          completed += file.size;
          onProgress?.call(completed, total);
          continue;
        }
        await destination.delete();
      }
      final part = File('${destination.path}.part');
      var existing = await part.exists() ? await part.length() : 0;
      if (existing >= file.size) {
        await part.delete();
        existing = 0;
      }
      final request = http.Request('GET', file.url)
        ..headers['Range'] = 'bytes=$existing-';
      final response = await client.send(request);
      if (response.statusCode != 200 && response.statusCode != 206) {
        throw HttpException(
            'Model download failed: HTTP ${response.statusCode}',
            uri: file.url);
      }
      final sink = part.openWrite(
          mode: existing > 0 && response.statusCode == 206
              ? FileMode.append
              : FileMode.write);
      var received = response.statusCode == 206 ? existing : 0;
      await for (final chunk in response.stream) {
        sink.add(chunk);
        received += chunk.length;
        onProgress?.call(completed + received, total);
      }
      await sink.flush();
      await sink.close();
      if (await part.length() != file.size) {
        throw StateError('Model file size mismatch: ${file.relativePath}');
      }
      final digest = await sha256.bind(part.openRead()).first;
      if (digest.toString().toLowerCase() != file.sha256.toLowerCase()) {
        await part.delete();
        throw StateError('Model checksum mismatch: ${file.relativePath}');
      }
      await part.rename(destination.path);
      completed += file.size;
    }
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

  Future<bool> verify(ModelFile file, Directory installed) async {
    final local = File(p.join(installed.path, file.relativePath));
    if (!await local.exists() || await local.length() != file.size) {
      return false;
    }
    return (await sha256.bind(local.openRead()).first)
            .toString()
            .toLowerCase() ==
        file.sha256.toLowerCase();
  }
}
