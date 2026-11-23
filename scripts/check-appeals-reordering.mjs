// Test-only delivery barriers. Every removed broker message is saved and republished in finally/release.
import assert from 'node:assert/strict';
import {readFile,writeFile} from 'node:fs/promises';
import {randomUUID} from 'node:crypto';
import {compose,sql,quote as q,eventually,management} from './transport-test-support.mjs';
const phase=process.argv[2], path='.local/e2e/held-appeal-facts.json';
if(phase==='prepare') {
  await eventually(()=>sql('appeals',"SELECT count(*) FROM outbox WHERE status <> 'Sent'"),x=>x==='0','accepted request published');
  compose(['stop','appeals-api']); compose(['start','checking-api']);
  console.log('Appeals stopped after durable 202/request confirm; Checking resumes the same request: OK');
} else if(phase==='withhold') {
  await eventually(async()=> (await management('queues/matigr/appeals.checking-facts.v1')).messages_ready,x=>x>=2,'actual intermediate facts available');
  const messages=await management('queues/matigr/appeals.checking-facts.v1/get','POST',{count:100,ackmode:'ack_requeue_false',encoding:'auto',truncate:1000000});
  assert.ok(messages.length>=2); await writeFile(path,JSON.stringify(messages));
  assert.ok((await management('queues/matigr/appeals.result-receipts.v1')).messages_ready>=1);
  compose(['start','appeals-api']);
  const ids=[...new Set(messages.map(x=>JSON.parse(x.payload).payload.appealId))];
  for(const id of ids) await eventually(()=>sql('appeals',`SELECT state FROM appeals WHERE appeal_id=${q(id)}`),x=>x==='Closed','early sufficient Applied closes without intermediate facts');
  console.log('Actual Results receipt delivered before captured Checking facts; Closed without intermediate Assigned/Completed: OK');
} else if(phase==='release') {
  const messages=JSON.parse(await readFile(path));
  const before=await sql('appeals',"SELECT json_agg(json_build_array(appeal_id,version,state) ORDER BY appeal_id)::text FROM appeals");
  for(const m of messages) for(const duplicate of [false,true]) {
    const e=JSON.parse(m.payload); if(duplicate)e.messageId=randomUUID();
    await management('exchanges/matigr/matigr.v1/publish','POST',{properties:{...m.properties,message_id:e.messageId},routing_key:m.routing_key,payload:JSON.stringify(e),payload_encoding:'string'});
    await eventually(()=>sql('appeals',`SELECT count(*) FROM inbox WHERE message_id=${q(e.messageId)}`),x=>x==='1','late fact inbox committed');
  }
  assert.equal(await sql('appeals',"SELECT json_agg(json_build_array(appeal_id,version,state) ORDER BY appeal_id)::text FROM appeals"),before);
  console.log('Captured late facts and new-message semantic duplicates cannot regress Closed/version or create another attempt: OK');
} else throw new Error('Use prepare / withhold / release explicitly');
