import assert from 'node:assert/strict';
import {sql,quote as q,compose,eventually,session} from './transport-test-support.mjs';
const phase=process.argv[2];
if(phase==='restart') {
 const keepAlive=setInterval(()=>{},1000);
 try {
 const before=await sql('checking',"SELECT json_agg(row_to_json(a))::text FROM assignments a WHERE state='Active'");assert.notEqual(before,'');
 compose(['restart','checking-api']);
 await eventually(async()=>{try{return(await fetch('http://127.0.0.1:5101/health/ready',{signal:AbortSignal.timeout(2000)})).status}catch{return 0}},x=>x===200,'Checking restart');
 assert.equal(await sql('checking',"SELECT json_agg(row_to_json(a))::text FROM assignments a WHERE state='Active'"),before);
 const check=await sql('checking',"SELECT check_id FROM checks WHERE state='SeniorAssigned'");const token=await session('jury-a');
 assert.equal((await fetch(`http://127.0.0.1:5101/api/v1/checking/checks/${check}/material`,{headers:{Authorization:`Bearer ${token}`}})).status,403);
 console.log('Senior assignment unchanged after Checking restart; released selected user material forbidden: OK');
 } finally {clearInterval(keepAlive);}
}else{
 const raw=await sql('appeals',"SELECT snapshot::text FROM appeals WHERE snapshot->'receipt'->>'origin'='SeniorEscalation'");assert.ok(raw);
 const a=JSON.parse(raw);assert.equal(a.state,'Closed');assert.equal(a.receipt.evaluationId,a.progress.checkId);assert.equal(a.attempt.reviewerId,a.receipt.selectedReviewerId);
 assert.notEqual(a.receipt.reviewerId,a.receipt.selectedReviewerId);assert.ok(a.receipt.escalationId);
 assert.equal(await sql('checking',`SELECT count(*) FROM checks WHERE appeal_id=${q(a.appealId)}`),'1');
 assert.equal(await sql('checking',`SELECT count(*) FROM assignments WHERE check_id=${q(a.receipt.evaluationId)}`),'2');
 assert.equal(await sql('appeals',`SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(a.appealId)}`),'1');
 console.log('Same Appeal/Check/request, immutable selected and actual senior/assignment/escalation, two historical assignments, one quota and Closed: OK');
}
