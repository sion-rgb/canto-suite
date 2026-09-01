import 'dart:convert';
import 'dart:io';

import 'package:canto_meet/core/model_download.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:path/path.dart' as p;

void main() {
  test('model install resumes a partial file, verifies it, and pins atomically',
      () async {
    final root = await Directory.systemTemp.createTemp('canto-model-test-');
    addTearDown(() => root.delete(recursive: true));
    final bytes = utf8.encode('verified-local-model');
    final staging = Directory(p.join(root.path, '.staging', 'model-revision'));
    await staging.create(recursive: true);
    await File(p.join(staging.path, 'model.bin.part'))
        .writeAsBytes(bytes.sublist(0, 8));
    String? range;
    final client = MockClient((request) async {
      range = request.headers['range'];
      return http.Response.bytes(bytes.sublist(8), 206);
    });
    final file = ModelFile(
        url: Uri.parse('https://models.invalid/model.bin'),
        relativePath: 'model.bin',
        sha256: sha256.convert(bytes).toString(),
        size: bytes.length);

    await ModelInstaller(root, client).install('model', 'revision', [file]);

    expect(range, 'bytes=8-');
    expect(
        await File(p.join(root.path, 'model', 'revision', 'model.bin'))
            .readAsBytes(),
        bytes);
    expect(await staging.exists(), isFalse);
    expect(
        jsonDecode(await File(p.join(root.path, 'model', 'current.json'))
            .readAsString())['version'],
        'revision');
  });

  test('checksum failure does not replace an existing installation', () async {
    final root = await Directory.systemTemp.createTemp('canto-model-test-');
    addTearDown(() => root.delete(recursive: true));
    final installed = Directory(p.join(root.path, 'model', 'revision'));
    await installed.create(recursive: true);
    final old = File(p.join(installed.path, 'model.bin'));
    await old.writeAsString('old');
    final bytes = utf8.encode('corrupt');
    final client = MockClient((_) async => http.Response.bytes(bytes, 200));
    final file = ModelFile(
        url: Uri.parse('https://models.invalid/model.bin'),
        relativePath: 'model.bin',
        sha256: sha256.convert(utf8.encode('expected')).toString(),
        size: bytes.length);

    await expectLater(
        ModelInstaller(root, client).install('model', 'revision', [file]),
        throwsStateError);
    expect(await old.readAsString(), 'old');
  });
}
