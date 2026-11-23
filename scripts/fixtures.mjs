import { readFile, realpath } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import assert from 'node:assert/strict';

export function validateManifest(m) {
  const version = /^(0|[1-9][0-9]*)$/;
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
  assert.match(m.fixtureVersion, version);
  assert.match(m.tournament.tournamentId, uuid);
  assert.equal(m.tournament.initialAppealWindow, 'Open');
  function unique(items, key) {
    const values = items.map(item => item[key]);
    assert.equal(new Set(values).size, values.length, `Duplicate ${key}`);
    return new Set(values);
  }
  const teams = unique(m.teams, 'teamId');
  const tasks = unique(m.tasks, 'taskId');
  unique(m.subjects, 'subjectId');
  unique(m.subjects, 'testSubjectId');
  unique(m.solutions, 'solutionId');
  unique(m.solutions, 'materialVersionId');
  unique(m.solutions, 'materialRef');
  unique(m.solutions, 'materialFile');
  unique(m.solutions.map(s => ({ tuple: `${s.tournamentId}/${s.teamId}/${s.taskId}` })), 'tuple');
  for (const task of m.tasks) {
    assert.match(task.taskId, uuid);
    assert.match(task.points, version);
    assert.match(task.scoreRuleVersion, version);
    assert.ok(BigInt(task.points) < 10n ** 28n, 'Points exceed numeric(28,0)');
  }
  for (const team of m.teams) assert.match(team.teamId, uuid);
  for (const subject of m.subjects) {
    assert.match(subject.subjectId, uuid);
    assert.deepEqual(subject.tournamentIds, [m.tournament.tournamentId]);
    assert.ok(subject.roles.length > 0 && subject.roles.every(r => ['Jury', 'Senior', 'Organizer', 'Team'].includes(r)));
    if (subject.roles.includes('Team')) assert.ok(teams.has(subject.teamId));
    else assert.equal(subject.teamId, undefined);
  }
  for (const solution of m.solutions) {
    assert.match(solution.solutionId, uuid);
    assert.match(solution.materialVersionId, uuid);
    assert.equal(solution.tournamentId, m.tournament.tournamentId);
    assert.ok(teams.has(solution.teamId) && tasks.has(solution.taskId), 'Unknown team/task');
    assert.match(solution.materialFile, /^materials\/[a-z0-9-]+\.txt$/);
    assert.match(solution.materialSha256, /^[a-f0-9]{64}$/);
    assert.match(solution.sourceVersion, /^[1-9][0-9]*$/);
    assert.match(solution.acceptedAt, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
    assert.ok(Number.isFinite(Date.parse(solution.acceptedAt)));
  }
  return m;
}

export async function loadFixtures(directory) {
  const bytes = await readFile(path.join(directory, 'manifest.json'));
  const hash = createHash('sha256').update(bytes).digest('hex');
  assert.equal(hash, (await readFile(path.join(directory, 'manifest.sha256'), 'utf8')).trim(), 'Manifest checksum mismatch');
  const manifest = validateManifest(JSON.parse(bytes));
  const canonicalRoot = await realpath(directory);
  for (const solution of manifest.solutions) {
    const file = await realpath(path.join(directory, solution.materialFile));
    const relative = path.relative(canonicalRoot, file);
    assert.ok(!relative.startsWith('..') && !path.isAbsolute(relative), 'Material escapes fixture allowlist');
    const actual = createHash('sha256').update(await readFile(file)).digest('hex');
    assert.equal(actual, solution.materialSha256, `Material checksum mismatch: ${solution.materialRef}`);
  }
  return { manifest, hash };
}
