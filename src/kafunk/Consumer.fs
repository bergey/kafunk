namespace Kafunk

open FSharp.Control
open System
open System.Threading
open System.Threading.Tasks

open Kafunk

/// Kafka consumer configuration.
type ConsumerConfig = {
  groupId : GroupId
  topics : TopicName[]
  sessionTimeout : SessionTimeout
  heartbeatFrequency : int32
  fetchMinBytes : MinBytes
  fetchMaxWaitMs : MaxWaitTime
  fetchBufferBytes : MaxBytes
  offsetRetentionTime : int64
  initialFetchTime : Time
} with
    static member create 
      (groupId:GroupId, topics:TopicName[], ?initialFetchTime, ?fetchBufferBytes, ?sessionTimeout, 
          ?heartbeatFrequency, ?offsetRetentionTime, ?fetchMinBytes, ?fetchMaxWaitMs) =
      {
        groupId = groupId
        topics = topics
        sessionTimeout = defaultArg sessionTimeout 20000
        heartbeatFrequency = defaultArg heartbeatFrequency 10
        fetchMinBytes = defaultArg fetchMinBytes 0
        fetchMaxWaitMs = defaultArg fetchMaxWaitMs 0
        fetchBufferBytes = defaultArg fetchBufferBytes 1000000
        offsetRetentionTime = defaultArg offsetRetentionTime -1L
        initialFetchTime = defaultArg initialFetchTime Time.EarliestOffset
      }

/// A consumer.
type Consumer = 
  private
  | Consumer of AsyncSeq<GenerationId * (TopicName * Partition * AsyncSeq<MessageSet * Async<unit>>)[]>

/// High-level consumer API.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Consumer =

  let private Log = Log.create "Kafunk.Consumer"
  
  let private generations (Consumer(gs)) = gs

  /// Stats corresponding to a single generation of the consumer group protocol.
  type GenerationState = {
    generationId : GenerationId
    memberId : MemberId
    leaderId : LeaderId
    assignments : TopicPartitionAssignment[]
    closed : Tasks.TaskCompletionSource<unit>
  } 

  and TopicPartitionAssignment = {
    topic : TopicName
    partition : Partition
    initOffset : Offset
  }

  // TODO: refactor (back to Cancellation token, or Hopac Alt?)
  let private peekTask (f:'a -> 'b) (t:Task<'a>) (a:Async<'b>) = async {
    if t.IsCompleted then return f t.Result
    else return! a }

  /// Creates a participant in the consumer groups protocol.
  /// A generation changes when the group changes, such as when members join or leave.
  /// Each generation contains a set of async sequences corresponding to messages in a single topic-partition.
  /// The message set is accompanied by an async computation which when evaluated, commits the checkpoint
  /// corresponding to the message set.
  let create (conn:KafkaConn) (cfg:ConsumerConfig) =
        
    let protocolType = ProtocolType.consumer
    let consumerProtocolMeta = ConsumerGroupProtocolMetadata(0s, cfg.topics, Binary.empty)
    let assignmentStrategy : AssignmentStrategy = "range" // roundrobin
    let groupProtocols =
      GroupProtocols(
        [| assignmentStrategy, toArraySeg ConsumerGroupProtocolMetadata.size ConsumerGroupProtocolMetadata.write consumerProtocolMeta |])
                            
    /// Joins the consumer group.
    /// - Join group.
    /// - Sync group (assign partitions to members).
    /// - Start heartbeats.
    /// - Fetch initial offsets.
    let rec join (prevMemberId:MemberId option) = async {
      
      match prevMemberId with
      | None -> 
        Log.info "initializing_consumer|group_id=%s" cfg.groupId

      | Some prevMemberId -> 
        Log.info "rejoining_consumer_group|group_id=%s member_id=%s" cfg.groupId prevMemberId
        do! conn.ReconnectChans ()

      let! _ = conn.GetGroupCoordinator (cfg.groupId)

      let! joinGroupRes = async {
        let initMemberId = defaultArg prevMemberId ""
        let req = JoinGroup.Request(cfg.groupId, cfg.sessionTimeout, initMemberId, protocolType, groupProtocols)
        let! res = Kafka.joinGroup conn req
        match res.errorCode with
        | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
          return Failure res.errorCode
        | _ ->
          return Success res }
      
      match joinGroupRes with
      | Failure ec ->
        Log.warn "join_group_error|error_code=%i" ec
        match ec with
        | ErrorCode.UnknownMemberIdCode -> 
          Log.warn "resetting_member_id"
          do! Async.Sleep cfg.sessionTimeout
          return! join None
        | _ -> 
          return! join prevMemberId

      | Success joinGroupRes ->
                           
        Log.info "join_group_response|group_id=%s member_id=%s generation_id=%i leader_id=%s group_protocol=%s" 
          cfg.groupId 
          joinGroupRes.memberId 
          joinGroupRes.generationId 
          joinGroupRes.leaderId
          joinGroupRes.groupProtocol
                          
        let! syncGroupRes = async {
          if joinGroupRes.members.members.Length > 0 then
            Log.info "joined_as_leader"
            let members = joinGroupRes.members.members
            let! topicPartitions = conn.GetMetadata cfg.topics
            // TODO: consider cases where there are more consumers than partitions
            let topicPartitions =
              topicPartitions
              |> Map.toSeq
              |> Seq.collect (fun (t,ps) -> ps |> Seq.map (fun p -> t,p))
              |> Seq.toArray
              |> Array.groupInto members.Length
            let memberAssignments =
              (members,topicPartitions)
              ||> Array.zip 
              |> Array.map (fun ((memberId,_),ps) ->
                let assignment = 
                  ps 
                  |> Seq.groupBy fst 
                  |> Seq.map (fun (tn,xs) -> tn, xs |> Seq.map snd |> Seq.toArray)
                  |> Seq.toArray
                let assignment = ConsumerGroupMemberAssignment(0s, PartitionAssignment(assignment))
                memberId, (toArraySeg ConsumerGroupMemberAssignment.size ConsumerGroupMemberAssignment.write assignment))
            let req = SyncGroupRequest(cfg.groupId, joinGroupRes.generationId, joinGroupRes.memberId, GroupAssignment(memberAssignments))
            let! res = Kafka.syncGroup conn req
            match res.errorCode with
            | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
              return None
            | _ ->
              return Some res
          else
            Log.info "joined_as_follower"
            let req = SyncGroupRequest(cfg.groupId, joinGroupRes.generationId, joinGroupRes.memberId, GroupAssignment([||]))
            let! res = Kafka.syncGroup conn req
            match res.errorCode with
            | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
              return None
            | _ ->
              return Some res }
        
        match syncGroupRes with
        | None ->
          return! join (Some joinGroupRes.memberId)

        | Some syncGroupRes ->
                
          let assignment,_ = 
            ConsumerGroupMemberAssignment.read syncGroupRes.memberAssignment

          Log.info "received_sync_group_response|member_assignment=[%s]"
            (String.concat ", " (assignment.partitionAssignment.assignments |> Seq.map (fun (tn,ps) -> sprintf "topic=%s partitions=%A" tn ps))) 
      
          if assignment.partitionAssignment.assignments.Length = 0 then
            failwith "no partitions assigned!"

          /// Fetches the starting offset for the specified topic * partition.
          let fetchInitOffset (tn:TopicName, p:Partition) = async {
            Log.info "fetching_group_member_offset|topic=%s partition=%i group_id=%s time=%i" tn p cfg.groupId cfg.initialFetchTime
            try
              let req = OffsetFetchRequest(cfg.groupId, [| tn, [| p |] |])
              let! res = Kafka.offsetFetch conn req
              let _topic,ps = res.topics.[0]
              let (_p,offset,_metadata,ec) = ps.[0]
              match ec with
              | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode  ->
                return failwith "restart join process"
              | _ ->
                if offset = -1L then
                  Log.info "offset_not_available_at_group_coordinator|group_id=%s member_id=%s topic=%s partition=%i generation=%i" cfg.groupId joinGroupRes.memberId tn p joinGroupRes.generationId
                  let offsetReq = OffsetRequest(-1, [| tn, [| p,cfg.initialFetchTime,1 |] |])
                  let! offsetRes = Kafka.offset conn offsetReq
                  let _,ps = offsetRes.topics.[0]
                  return ps.[0].offsets.[0]
                else
                  return offset
            with ex ->
              Log.error "fetch_offset_error|error=%O" ex
              return raise ex }

          let! initOffsets =
            assignment.partitionAssignment.assignments
            |> Seq.collect (fun (tn,ps) -> ps |> Seq.map (fun p -> tn,p))
            |> Seq.map (fun (tn,p) -> async {
              let! offset = fetchInitOffset (tn,p)
              return { topic = tn ; partition = p ; initOffset = offset } })
            |> Async.Parallel
        
          Log.info "fetched_initial_offsets|"

          let state =
            {
              memberId = joinGroupRes.memberId
              leaderId = joinGroupRes.leaderId
              generationId = joinGroupRes.generationId
              assignments = initOffsets
              closed = Tasks.TaskCompletionSource<unit>()
            }
          
          conn.CancellationToken.Register (fun () ->
            Log.info "closing_consumer_group_on_connection_close" 
            state.closed.TrySetResult() |> ignore) |> ignore

          let heartbeatSleep = cfg.sessionTimeout / cfg.heartbeatFrequency

          /// Starts the hearbeat process.
          let rec heartbeat (state:GenerationState) =
            peekTask
              id
              state.closed.Task
              (async {
                let req = HeartbeatRequest(cfg.groupId, state.generationId, state.memberId)
                let! res = Kafka.heartbeat conn req |> Async.Catch
                match res with
                | Success res ->
                  match res.errorCode with
                  | ErrorCode.IllegalGenerationCode | ErrorCode.UnknownMemberIdCode | ErrorCode.RebalanceInProgressCode ->
                    do! close state
                    return ()
                  | _ ->
                    do! Async.Sleep heartbeatSleep
                    return! heartbeat state
                | Failure ex ->
                  Log.warn "heartbeat_failure|generation_id=%i error=%O" state.generationId ex
                  do! close state
                  return () })

          Log.info "starting_heartbeats|heartbeat_frequency=%i session_timeout=%i heartbeat_sleep=%i" cfg.heartbeatFrequency cfg.sessionTimeout heartbeatSleep
          Async.Start (heartbeat state)

          return state }

    /// Closes a consumer group.
    /// The current generation stops emitting.
    /// A new join operation will begin.
    and close (state:GenerationState) : Async<unit> =
      peekTask
        id
        state.closed.Task
        (async {
          if state.closed.TrySetResult () then
            Log.warn "closing_consumer_group|generation_id=%i member_id=%s leader_id=%s" state.generationId state.memberId state.leaderId
          else
            Log.warn "concurrent_close_request_received|generation_id=%i member_id=%s leader_id=%s" state.generationId state.memberId state.leaderId
          })

    /// Initiates consumption of a single generation of the consumer group protocol.
    let consume (state:GenerationState) = async {

      /// Returns an async sequence corresponding to the consumption of an individual partition.
      let consumePartition (topic:TopicName, partition:Partition, initOffset:FetchOffset) = async {
           
        /// Commits the specified offset within the consumer group.
        let commitOffset (offset:FetchOffset) : Async<unit> = 
          peekTask
            (ignore)
            (state.closed.Task)
            (async {
              //Log.trace "committing_offset|topic=%s partition=%i group_id=%s member_id=%s generation_id=%i offset=%i retention=%i" topic partition cfg.groupId state.memberId state.generationId offset cfg.offsetRetentionTime
              let req = OffsetCommitRequest(cfg.groupId, state.generationId, state.memberId, cfg.offsetRetentionTime, [| topic, [| partition, offset, "" |] |])
              let! res = Kafka.offsetCommit conn req |> Async.Catch
              match res with
              | Success res ->
                if res.topics.Length > 0 then
                  let (_tn,ps) = res.topics.[0]
                  let (_p,ec) = ps.[0]
                  match ec with
                  | ErrorCode.IllegalGenerationCode | ErrorCode.UnknownMemberIdCode | ErrorCode.RebalanceInProgressCode  ->
                    do! close state
                    return ()
                  | _ ->
                    //Log.trace "offset_comitted|topic=%s partition=%i group_id=%s member_id=%s generation_id=%i offset=%i" tn p cfg.groupId state.memberId state.generationId offset
                    return ()
                else
                  Log.error "offset_committ_failed|topic=%s partition=%i group_id=%s member_id=%s generation_id=%i offset=%i" topic partition cfg.groupId state.memberId state.generationId offset
                  return failwith "offset commit failed!"
              | Failure ex ->
                Log.warn "commit_offset_failure|generation_id=%i error=%O" state.generationId ex
                do! close state
                return () })

        /// Fetches the specified offset.
        let rec fetch (offset:FetchOffset) : Async<FetchResponse option> = 
          peekTask
            (fun _ -> None)
            (state.closed.Task)
            (async {
              let req = FetchRequest(-1, cfg.fetchMaxWaitMs, cfg.fetchMinBytes, [| topic, [| partition, offset, cfg.fetchBufferBytes |] |])
              let! res = Kafka.fetch conn req |> Async.Catch
              match res with
              | Success res ->
                if res.topics.Length = 0 then
                  return failwith "nothing returned in fetch response!"
                else
                  let _,partitions = res.topics.[0]
                  let _,ec,highWatermarkOffset,_mss,ms = partitions.[0]
                  match ec with
                  | ErrorCode.OffsetOutOfRange | ErrorCode.UnknownTopicOrPartition | ErrorCode.NotLeaderForPartition ->
                    Log.warn "consumer_group_fetch_error|error_code=%i" ec
                    do! close state
                    return None
                  | _ ->
                    if ms.messages.Length = 0 then
                      Log.info "reached_end_of_stream|topic=%s partition=%i offset=%i high_watermark_offset=%i" topic partition offset highWatermarkOffset
                      do! Async.Sleep 10000
                      return! fetch offset 
                    else
                      //let ms = Compression.decompress ms
                      return Some res
              | Failure ex ->
                Log.warn "fetch_failure|generation_id=%i error=%O" state.generationId ex
                do! close state
                return None })

        let rec fetch2 (offset:FetchOffset) = async {
          try
            return! fetch offset
          with 
            | EscalationException (ec,res) when ec = ErrorCode.OffsetOutOfRange ->
              Log.warn "offset_exception_escalated|topic=%s partition=%i error_code=%i res=%A" topic partition ec res
              let offsetReq = OffsetRequest(-1, [| topic, [| partition, cfg.initialFetchTime, 1 |] |])
              let! offsetRes = Kafka.offset conn offsetReq
              let (tn,ps) = offsetRes.topics.[0]
              let p = ps.[0]
              Log.warn "fetched_topic_offsets|topic=%s partition=%i latest_offset=%i attempted_offset=%i delta=%i" tn p.partition p.offsets.[0] offset (p.offsets.[0] - offset)
              do! Async.Sleep 5000
              return! fetch2 p.offsets.[0] }

        let fetch = fetch2

        /// Fetches a stream of messages starting at the specified offset.
        let fetchStream =
          AsyncSeq.unfoldAsync
            (fun (offset:FetchOffset) -> async {
              let! res = fetch offset
              match res with
              | None ->
                return None
              | Some res ->
                let _,partitions = res.topics.[0]
                let _,_ec,highWatermarkOffset,_mss,ms = partitions.[0]
                //let ms = Compression.decompress ms
                let commit = commitOffset offset
                let nextOffset = MessageSet.nextOffset ms highWatermarkOffset
                return Some ((ms,commit), nextOffset) })
      
        return fetchStream initOffset }
     
      let! partitionStreams =
        state.assignments
        |> Seq.map (fun a -> async {
          let! stream = consumePartition (a.topic,a.partition,a.initOffset)
          return a.topic,a.partition,stream })
        |> Async.Parallel

      return partitionStreams }

    /// Emits generations of the consumer group protocol.
    let generations =
      AsyncSeq.unfoldAsync
        (fun (prevState:GenerationState option) -> async {
          match prevState with
          | None -> ()
          | Some prevState ->
            Log.warn "waiting_on_generation_to_complete|generation_id=%i member_id=%s" prevState.generationId prevState.memberId
            do! prevState.closed.Task |> Async.AwaitTask
          let! state = join (prevState |> Option.map (fun s -> s.memberId))
          let! topics = consume state
          return Some ((state.generationId,topics), Some state) })
                   
    Consumer (generations None)

  /// Starts consumption using the specified handler.
  /// The handler will be invoked in parallel across topic/partitions, but sequentially within a topic/partition.
  /// The handler accepts the topic, partition, message set and an async computation which commits offsets corresponding to the message set.
  let consume 
    (handler:TopicName -> Partition -> MessageSet -> Async<unit> -> Async<unit>) 
    (consumer:Consumer) : Async<unit> =
      consumer
      |> generations 
      |> AsyncSeq.iterAsync (fun (_generationId,topics) -> async {
        return!
          topics
          |> Seq.map (fun (tn,p,stream) -> async {
            return! stream |> AsyncSeq.iterAsync (fun (ms,commit) -> handler tn p ms commit) })
          |> Async.Parallel
          |> Async.Ignore })

  /// Starts consumption using the specified handler.
  /// Offsets for the corresponding topic/partition/message-set are committed after the handler completes.
  let consumeCommitAfter 
    (handler:TopicName -> Partition -> MessageSet -> Async<unit>) =
    consume (fun tn p ms commit -> async {
        do! handler tn p ms
        do! commit })

    

  