﻿#r "bin/release/fsharp.control.asyncseq.dll"
#r "bin/Release/kafunk.dll"
#time "on"

open FSharp.Control
open Kafunk

let conn = Kafka.connHost "127.0.0.1:9092" 
let topicName = "test-topic_1019"

let producerCfg =
  ProducerConfig.create (topicName, Partitioner.roundRobin, requiredAcks=RequiredAcks.Local)

let producer =
  Producer.createAsync conn producerCfg
  |> Async.RunSynchronously


//let prodRes =
//  Producer.produceSingle producer (topicName, [| ProducerMessage.ofBytes "world"B |])
//  |> Async.RunSynchronously
//
//for (tn,offsets) in prodRes.topics do
//  printfn "topic_name=%s" tn
//  for (p,ec,offset) in offsets do
//    printfn "partition=%i error_code=%i offset=%i" p ec offset


let N = 10000000

Seq.init N id
|> Seq.map (fun i -> async {

  let payload = Array.zeroCreate 10

  //printfn "sending request=%i" i

  let! prodRes =
    Producer.produce producer [| ProducerMessage.ofBytes payload |]

  return ()

  //printfn "received produce response=%i" i
//  for (tn,offsets) in prodRes.topics do
//    printfn "topic_name=%s" tn
//    for (p,ec,offset) in offsets do
//      printfn "partition=%i error_code=%i offset=%i" p ec offset

})
|> Async.ParallelIgnore 10
|> Async.RunSynchronously