import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const source = readFileSync(new URL('../src/utils/execution.ts', import.meta.url), 'utf8')
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { restoreMessages } = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'))
test('history restores code parents, file traces and failure states without raw tool messages', () => {
  const messages = restoreMessages([{id:'a',role:'assistant',content:'',toolCallsJson:JSON.stringify([{id:'code',function:{name:'execute_code',arguments:'{"code":"print(1)"}'}}])}, {id:'t',role:'tool',toolCallId:'code',content:'{"ok":false,"error":"failed"}',hostToolTraceJson:JSON.stringify([{id:'file',toolName:'file_edit',argsJson:'{"path":"a.txt"}',resultJson:'{"ok":true,"data":{"diff":{"added":1,"removed":1}}}'}])}])
  assert.equal(messages.length, 1)
  assert.equal(messages[0].toolCalls[0].status, 'error')
  assert.equal(messages[0].toolCalls[1].parentId, 'code')
  assert.equal(messages[0].toolCalls[1].name, 'file_edit')
  assert.match(messages[0].toolCalls[1].output, /diff/)
})
test('legacy and malformed history stays readable', () => {
  assert.deepEqual(restoreMessages([{id:'a',role:'assistant',content:'answer',toolCallsJson:'broken'}])[0].toolCalls, [])
  const history = restoreMessages([{id:'a',role:'assistant',content:'',toolCallsJson:'[{"id":"code","function":{"name":"execute_code","arguments":"{}"}}]'}])
  assert.equal(history[0].toolCalls[0].status, 'error')
})
