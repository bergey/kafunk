(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../src/kafunk/bin/Release"

(**
Kafunk - F# Kafka client
======================

Example
-------

This example demonstrates a few uses of the Kafka client.

*)

#r "kafunk.dll"
open Kafunk


let conn = Kafka.connHost "existential-host"


// metadata

let metadata = 
  Kafka.metadata conn (Metadata.Request([|"absurd-topic"|])) 
  |> Async.RunSynchronously

for b in metadata.brokers do
  printfn "broker|host=%s port=%i nodeId=%i" b.host b.port b.nodeId

for t in metadata.topicMetadata do
  printfn "topic|topic_name=%s topic_error_code=%i" t.topicName t.topicErrorCode
  for p in t.partitionMetadata do
    printfn "topic|topic_name=%s|partition|partition_id=%i" t.topicName p.partitionId


// producer

let producerCfg =
  ProducerConfig.create ("absurd-topic", Partitioner.roundRobin, requiredAcks = RequiredAcks.Local)

let producer =
  Producer.createAsync conn producerCfg
  |> Async.RunSynchronously

let prodRes =
  Producer.produce producer [| ProducerMessage.ofBytes ("hello world"B) |]
  |> Async.RunSynchronously

for (tn,offsets) in prodRes.topics do
  printfn "topic_name=%s" tn
  for (p,ec,offset) in offsets do
    printfn "partition=%i error_code=%i offset=%i" p ec offset


// consumer

let consumerCfg = 
  ConsumerConfig.create ("consumer-group", [|"absurd-topic"|])

let consumer =
  Consumer.create conn consumerCfg

consumer
|> Consumer.consume (fun tn p ms commit -> async {
  printfn "topic=%s partition=%i" tn p
  do! commit })
|> Async.RunSynchronously  



(**
 
Contributing and copyright
--------------------------

The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests. If you're adding a new public API, please also 
consider adding [samples][content] that can be turned into a documentation. You might
also want to read the [library design notes][readme] to understand how it works.

The library is available under Apache 2.0. For more information see the 
[License file][license] in the GitHub repository. 

  [content]: https://github.com/jet/kafunk/tree/master/docs/content
  [gh]: https://github.com/jet/kafunk
  [issues]: https://github.com/jet/kafunk/issues
  [readme]: https://github.com/jet/kafunk/blob/master/project/README.md
  [license]: https://github.com/jet/kafunk/blob/master/project/LICENSE.txt

*)
