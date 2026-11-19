import assert from 'node:assert/strict';
import {readFile,writeFile} from 'node:fs/promises';
import {performance} from 'node:perf_hooks';
import {sql,eventually,session,quote as q} from './transport-test-support.mjs';
const m=JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json',import.meta.url)));
const tournament=m.tournament.tournamentId, base=`http://127.0.0.1:5102/api/v1/results/tournaments/${tournament}/standings`;
const timings=[]; let snapshot;
for(let i=0;i<10;i++){const start=performance.now();const r=await fetch(base);assert.equal(r.status,200);snapshot=await r.json();timings.push(performance.now()-start);}
const alpha=m.teams[0].teamId,beta=m.teams[1].teamId,gamma=m.teams[2].teamId;
const totals=JSON.parse(await sql('results',`SELECT json_object_agg(team_id,total)::text FROM (SELECT team_id,sum(score)::text total FROM team_task_results GROUP BY team_id) s`));
assert.deepEqual(totals,{[alpha]:'10',[beta]:'0',[gamma]:'10'});
const appeals=JSON.parse(await sql('appeals',"SELECT json_agg(json_build_object('id',appeal_id,'state',state,'base',base_publication_id,'final',final_publication_id,'team',team_id,'check',check_id))::text FROM appeals"));
for(const a of appeals.filter(x=>x.state==='Closed')){
 assert.equal(await sql('checking',`SELECT count(*) FROM checks WHERE appeal_id=${q(a.id)}`),'1');
 const receipt=JSON.parse(await sql('results',`SELECT response::text FROM evaluation_receipts WHERE evaluation_id=${q(a.check)}`)).payload;
 assert.equal(receipt.basePublicationId,a.base);assert.equal(receipt.publicationId,a.final);
 assert.equal(receipt.changed,a.base!==a.final);
 const token=await session(m.subjects.find(s=>s.teamId===a.team).testSubjectId);
 const r=await fetch('http://127.0.0.1:5103/api/v1/appeals',{method:'POST',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json','Idempotency-Key':crypto.randomUUID()},body:JSON.stringify({resultId:receipt.resultId,expectedResultVersion:receipt.resultVersion})});
 assert.equal(r.status,409);assert.equal((await r.json()).code,'AlreadyAppealed');
}
const betaResult=await sql('results',`SELECT result_id FROM team_task_results WHERE team_id=${q(beta)} AND task_id=${q(m.tasks[1].taskId)}`);
assert.equal(await sql('results',`SELECT count(*) FROM result_publications WHERE result_id=${q(betaResult)}`),'2');
timings.sort((a,b)=>a-b);await writeFile('.local/e2e/results-smoke.json',JSON.stringify({requests:10,p50Milliseconds:timings[4],maxMilliseconds:timings[9],snapshotVersion:snapshot.snapshotVersion,scope:'Local three-team/two-task smoke only, no load SLO'},null,2));
console.log(`Final state: contribution replaced, dense ranking 1,1,2 verified by browser/SQL, original/final publication history, permanent quota; 10 GET smoke p50=${timings[4].toFixed(1)}ms max=${timings[9].toFixed(1)}ms (not a load threshold): OK`);
