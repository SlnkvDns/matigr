import assert from 'node:assert/strict';
import {readFile,writeFile} from 'node:fs/promises';
import {randomUUID} from 'node:crypto';
import {session,sql,quote as q,compose,eventually} from './transport-test-support.mjs';
const m=JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json',import.meta.url))),t=m.tournament.tournamentId;
const team=await session('team-beta'),organizer=await session('organizer');
async function post(token,path,body,key){return fetch('http://127.0.0.1:5103'+path,{method:'POST',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json','Idempotency-Key':key},body:JSON.stringify(body)});}
const file='.local/e2e/manual-end-operation.json',submit='/api/v1/appeals',end=`/dev/v1/tournaments/${t}/end-facts`;
if(process.argv[2]==='prepare'){
 const result=await sql('results',`SELECT result_id FROM team_task_results WHERE team_id=${q(m.teams[1].teamId)} AND task_id=${q(m.tasks[0].taskId)}`);
 const operation={body:{resultId:result,expectedResultVersion:'1'},key:randomUUID(),fact:{factId:randomUUID()},endKey:randomUUID()};
 const r=await post(team,submit,operation.body,operation.key);assert.equal(r.status,201);operation.reply=await r.json();assert.equal(operation.reply.state,'Registered');
 assert.equal((await post(team,end,operation.fact,randomUUID())).status,403);
 const e=await post(organizer,end,operation.fact,operation.endKey);assert.equal(e.status,200);operation.endReply=await e.json();
 await writeFile(file,JSON.stringify(operation));
 assert.equal(await sql('appeals',`SELECT state FROM submission_windows WHERE tournament_id=${q(t)}`),'Closed');
 assert.equal((await post(team,submit,operation.body,operation.key)).status,201);
 console.log('Timely Beta task1 Registered, organizer-only manual end, original Submit replay201 after Closed: OK');
}else{
 const o=JSON.parse(await readFile(file));const r=await post(team,submit,o.body,o.key);assert.equal(r.status,201);assert.deepEqual(await r.json(),o.reply);
 const e=await post(organizer,end,o.fact,o.endKey);assert.equal(e.status,200);assert.deepEqual(await e.json(),o.endReply);
 const gamma=await session('team-gamma');const late=await sql('results',`SELECT result_id FROM team_task_results WHERE team_id=${q(m.teams[2].teamId)} AND task_id=${q(m.tasks[1].taskId)}`);assert.ok(late);
 const denied=await post(gamma,submit,{resultId:late,expectedResultVersion:'1'},randomUUID());assert.equal(denied.status,409);assert.equal((await denied.json()).code,'WindowClosed');
 const keepAlive=setInterval(()=>{},1000);try{compose(['restart','appeals-api']);await eventually(async()=>{try{return(await fetch('http://127.0.0.1:5103/health/ready',{signal:AbortSignal.timeout(2000)})).status}catch{return 0}},x=>x===200,'Appeals bootstrap restart');}finally{clearInterval(keepAlive);}
 assert.equal(await sql('appeals',`SELECT state FROM submission_windows WHERE tournament_id=${q(t)}`),'Closed');
 await eventually(()=>sql('appeals',`SELECT state FROM appeals WHERE appeal_id=${q(o.reply.appealId)}`),x=>x==='Closed','timely work closes after end');
 assert.equal(await sql('appeals',`SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(o.reply.appealId)}`),'2');
 assert.equal(await sql('checking',`SELECT count(*) FROM checks WHERE appeal_id=${q(o.reply.appealId)}`),'1');
 assert.equal(await sql('appeals',`SELECT count(*) FROM appeals WHERE tournament_id=${q(t)} AND team_id=${q(m.teams[2].teamId)} AND task_id=${q(m.tasks[1].taskId)}`),'0');
 const ended=await sql('appeals',`SELECT ended_at FROM submission_windows WHERE tournament_id=${q(t)}`);
 const primary=JSON.parse(await sql('results',`SELECT publication::text FROM team_task_results WHERE result_id=${q(late)}`));
 assert.ok(Date.parse(primary.publishedAt)>Date.parse(ended));
 const prepared=m.solutions.find(s=>s.teamId===m.teams[2].teamId&&s.taskId===m.tasks[1].taskId);
 assert.equal(await sql('checking',`SELECT count(*) FROM checks WHERE solution_id=${q(prepared.solutionId)} AND kind='Initial' AND state='Completed' AND created_at<${q(ended)}::timestamptz AND completed_at>${q(ended)}::timestamptz`),'1');
 const final=JSON.parse(await sql('appeals',`SELECT snapshot::text FROM appeals WHERE appeal_id=${q(o.reply.appealId)}`));
 assert.equal(final.receipt.origin,'SeniorEscalation');assert.notEqual(final.receipt.reviewerId,final.receipt.selectedReviewerId);assert.ok(final.receipt.escalationId);
 assert.equal(final.receipt.requestId,final.attempt.requestId);
 console.log('After end: old successful Submit/end replay unchanged even after Closed; late new Submit denied; busy retry two attempts/one Check; restart bootstrap keeps Closed: OK');
}
