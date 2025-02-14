using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;

namespace Mono.Debugger.Soft
{
	public class VirtualMachine : Mirror
	{
		Queue queue;
		object queue_monitor;
		object startup_monitor;
		AppDomainMirror root_domain;
		Dictionary<int, EventRequest> requests;
		ITargetProcess process;

		internal Connection conn;

		VersionInfo version;

		internal VirtualMachine (ITargetProcess process, Connection conn) : base () {
			SetVirtualMachine (this);
			queue = new Queue ();
			queue_monitor = new Object ();
			startup_monitor = new Object ();
			requests = new Dictionary <int, EventRequest> ();
			this.conn = conn;
			this.process = process;
			conn.ErrorHandler += ErrorHandler;
		}

		// The standard output of the process is available normally through Process
		public StreamReader StandardOutput { get; set; }
		public StreamReader StandardError { get; set; }

		
		public Process Process {
			get {
				ProcessWrapper pw = process as ProcessWrapper;
				if (pw == null)
				    throw new InvalidOperationException ("Process instance not available");
				return pw.Process;
			}
		}

		public ITargetProcess TargetProcess {
			get {
				return process;
			}
		}

		public AppDomainMirror RootDomain {
			get {
				return root_domain;
			}
	    }

		public EndPoint EndPoint {
			get {
				var tcpConn = conn as TcpConnection;
				if (tcpConn != null)
					return tcpConn.EndPoint;
				return null;
			}
		}

		public VersionInfo Version {
			get {
				return version;
			}
		}

		EventSet current_es;
		int current_es_index;

		/*
		 * It is impossible to determine when to resume when using this method, since
		 * the debuggee is suspended only once per event-set, not event.
		 */
		[Obsolete ("Use GetNextEventSet () instead")]
		public Event GetNextEvent () {
			lock (queue_monitor) {
				if (current_es == null || current_es_index == current_es.Events.Length) {
					if (queue.Count == 0)
						Monitor.Wait (queue_monitor);
					current_es = (EventSet)queue.Dequeue ();
					current_es_index = 0;
				}
				return current_es.Events [current_es_index ++];
			}
		}

		public Event GetNextEvent (int timeout) {
			throw new NotImplementedException ();
		}

		public EventSet GetNextEventSet () {
			lock (queue_monitor) {
				if (queue.Count == 0)
					Monitor.Wait (queue_monitor);

				current_es = null;
				current_es_index = 0;

				return (EventSet)queue.Dequeue ();
			}
		}

		public EventSet GetNextEventSet (int timeoutInMilliseconds) {
			lock (queue_monitor) {
				if (queue.Count == 0) {
					if (!Monitor.Wait (queue_monitor, timeoutInMilliseconds)) {
						return null;
					}
				}

				current_es = null;
				current_es_index = 0;

				return (EventSet)queue.Dequeue ();
			}
		}

		[Obsolete ("Use GetNextEventSet () instead")]
		public T GetNextEvent<T> () where T : Event {
			return GetNextEvent () as T;
		}

		public void Suspend () {
			conn.VM_Suspend ();
	    }

		public void Resume () {
			try {
				InvalidateThreadAndFrameCaches ();
				conn.VM_Resume ();
			} catch (CommandException ex) {
				if (ex.ErrorCode == ErrorCode.NOT_SUSPENDED)
					throw new VMNotSuspendedException ();

				throw;
			}
	    }

		public void Exit (int exitCode) {
			conn.VM_Exit (exitCode);
		}

		public void Detach () {
			// Notify the application that we are detaching
			conn.VM_Dispose ();
			// Close the connection. No further messages can be sent
			// over the connection after this point.
			conn.Close ();
			notify_vm_event (EventType.VMDisconnect, SuspendPolicy.None, 0, 0, null, 0);
		}

		[Obsolete ("This method was poorly named; use the Detach() method instead")]
		public void Dispose ()
		{
			Detach ();
		}

		public void ForceDisconnect ()
		{
			conn.ForceDisconnect ();
		}

		HashSet<ThreadMirror> threadsToInvalidate = new HashSet<ThreadMirror> ();
		ThreadMirror[] threadCache;
		
		void InvalidateThreadAndFrameCaches () {
			lock (threadsToInvalidate) {
				foreach (var thread in threadsToInvalidate)
					thread.InvalidateFrames ();
				threadsToInvalidate.Clear ();
			}
			threadCache = null;
		}

		internal void InvalidateThreadCache () {
			threadCache = null;
		}

		internal void AddThreadToInvalidateList (ThreadMirror threadMirror)
		{
			lock (threadsToInvalidate) {
				threadsToInvalidate.Add (threadMirror);
			}
		}

		public IList<ThreadMirror> GetThreads () {
			var threads = threadCache;
			if (threads == null) {
				long[] ids = null;
				var fetchingEvent = new ManualResetEvent (false);
				vm.conn.VM_GetThreads ((threadsIds) => {
					ids = threadsIds;
					threads = new ThreadMirror [threadsIds.Length];
					fetchingEvent.Set ();
				});
				if (WaitHandle.WaitAny (new []{ vm.conn.DisconnectedEvent, fetchingEvent }) == 0) {
					throw new VMDisconnectedException ();
				}
				for (int i = 0; i < ids.Length; ++i)
					threads [i] = GetThread (ids [i]);
				//Uncomment lines below if you want to re-fetch threads if new threads were started/stopped while
				//featching threads... This is probably more correct but might cause deadlock of this method if runtime
				//is starting/stopping threads nonstop, need way to prevent this(counting number of recursions?)
				//possiblity before uncommenting
				//if (threadCache != threads) {//While fetching threads threadCache was invalidated(thread was created/destoyed)
				//	return GetThreads ();
				//}
				Thread.MemoryBarrier ();
				threadCache = threads;
				return threads;
			} else {
				return threads;
			}
		}

		// Same as the mirrorOf methods in JDI
		public PrimitiveValue CreateValue (object value) {
			if (value == null)
				return new PrimitiveValue (vm, null);

			if (!value.GetType ().IsPrimitive)
				throw new ArgumentException ("value must be of a primitive type instead of '" + value.GetType () + "'", "value");

			return new PrimitiveValue (vm, value);
		}

		public EnumMirror CreateEnumMirror (TypeMirror type, PrimitiveValue value) {
			return new EnumMirror (this, type, value);
		}

		//
		// Enable send and receive timeouts on the connection and send a keepalive event
		// every 'keepalive_interval' milliseconds.
		//

		public void SetSocketTimeouts (int send_timeout, int receive_timeout, int keepalive_interval)
		{
			conn.SetSocketTimeouts (send_timeout, receive_timeout, keepalive_interval);
		}

		//
		// Methods to create event request objects
		//
		public BreakpointEventRequest CreateBreakpointRequest (MethodMirror method, long il_offset) {
			return new BreakpointEventRequest (this, method, il_offset);
		}

		public BreakpointEventRequest CreateBreakpointRequest (Location loc) {
			if (loc == null)
				throw new ArgumentNullException ("loc");
			CheckMirror (loc);
			return new BreakpointEventRequest (this, loc.Method, loc.ILOffset);
		}

		public StepEventRequest CreateStepRequest (ThreadMirror thread) {
			return new StepEventRequest (this, thread);
		}

		public MethodEntryEventRequest CreateMethodEntryRequest () {
			return new MethodEntryEventRequest (this);
		}

		public MethodExitEventRequest CreateMethodExitRequest () {
			return new MethodExitEventRequest (this);
		}

		public ExceptionEventRequest CreateExceptionRequest (TypeMirror exc_type) {
			return new ExceptionEventRequest (this, exc_type, true, true);
		}

		public ExceptionEventRequest CreateExceptionRequest (TypeMirror exc_type, bool caught, bool uncaught) {
			return new ExceptionEventRequest (this, exc_type, caught, uncaught);
		}

		public ExceptionEventRequest CreateExceptionRequest (TypeMirror exc_type, bool caught, bool uncaught, bool everything_else) {
			if (Version.AtLeast (2, 54))
				return new ExceptionEventRequest (this, exc_type, caught, uncaught, true, everything_else);
			else
				return new ExceptionEventRequest (this, exc_type, caught, uncaught);
		}

		public AssemblyLoadEventRequest CreateAssemblyLoadRequest () {
			return new AssemblyLoadEventRequest (this);
		}

		public TypeLoadEventRequest CreateTypeLoadRequest () {
			return new TypeLoadEventRequest (this);
		}

		public void EnableEvents (params EventType[] events) {
			EnableEvents (events, SuspendPolicy.All);
		}

		public void EnableEvents (EventType[] events, SuspendPolicy suspendPolicy) {
			foreach (EventType etype in events) {
				if (etype == EventType.Breakpoint)
					throw new ArgumentException ("Breakpoint events cannot be requested using EnableEvents", "events");
				conn.EnableEvent (etype, suspendPolicy, null);
			}
		}

		public BreakpointEventRequest SetBreakpoint (MethodMirror method, long il_offset) {
			BreakpointEventRequest req = CreateBreakpointRequest (method, il_offset);

			req.Enable ();

			return req;
		}

		public void ClearAllBreakpoints () {
			conn.ClearAllBreakpoints ();
		}
		
		public void Disconnect () {
			conn.Close ();
		}

		//
		// Return a list of TypeMirror objects for all loaded types which reference the
		// source file FNAME. Might return false positives.
		// Since protocol version 2.7.
		//
		public IList<TypeMirror> GetTypesForSourceFile (string fname, bool ignoreCase) {
			long[] ids = conn.VM_GetTypesForSourceFile (fname, ignoreCase);
			var res = new TypeMirror [ids.Length];
			for (int i = 0; i < ids.Length; ++i)
				res [i] = GetType (ids [i]);
			return res;
		}

		//
		// Return a list of TypeMirror objects for all loaded types named 'NAME'.
		// NAME should be in the the same for as with Assembly.GetType ().
		// Since protocol version 2.9.
		//
		public IList<TypeMirror> GetTypes (string name, bool ignoreCase) {
			long[] ids = conn.VM_GetTypes (name, ignoreCase);
			var res = new TypeMirror [ids.Length];
			for (int i = 0; i < ids.Length; ++i)
				res [i] = GetType (ids [i]);
			return res;
		}
		
		internal void queue_event_set (EventSet es) {
			lock (queue_monitor) {
				queue.Enqueue (es);
				Monitor.Pulse (queue_monitor);
			}
		}

		internal void ErrorHandler (object sender, ErrorHandlerEventArgs args) {
			switch (args.ErrorCode) {
			case ErrorCode.INVALID_OBJECT:
				throw new ObjectCollectedException ();
			case ErrorCode.INVALID_FRAMEID:
				throw new InvalidStackFrameException ();
			case ErrorCode.NOT_SUSPENDED:
				throw new VMNotSuspendedException ();
			case ErrorCode.NOT_IMPLEMENTED:
				throw new NotSupportedException ("This request is not supported by the protocol version implemented by the debuggee.");
			case ErrorCode.ABSENT_INFORMATION:
				throw new AbsentInformationException ();
			case ErrorCode.NO_SEQ_POINT_AT_IL_OFFSET:
				throw new ArgumentException ("Cannot set breakpoint on the specified IL offset.");
			default:
				throw new CommandException (args.ErrorCode, args.ErrorMessage);
			}
		}

		/* Wait for the debuggee to start up and connect to it */
		internal void connect () {
			conn.Connect ();

			// Test the connection
			version = conn.Version;
			if (version.MajorVersion != Connection.MAJOR_VERSION)
				throw new NotSupportedException (String.Format ("The debuggee implements protocol version {0}.{1}, while {2}.{3} is required.", version.MajorVersion, version.MinorVersion, Connection.MAJOR_VERSION, Connection.MINOR_VERSION));

			long root_domain_id = conn.RootDomain;
			root_domain = GetDomain (root_domain_id);
		}

		internal void notify_vm_event (EventType evtype, SuspendPolicy spolicy, int req_id, long thread_id, string vm_uri, int exit_code) {
			//Console.WriteLine ("Event: " + evtype + "(" + vm_uri + ")");

			switch (evtype) {
			case EventType.VMStart:
				/* Notify the main thread that the debuggee started up */
				lock (startup_monitor) {
					Monitor.Pulse (startup_monitor);
				}
				queue_event_set (new EventSet (this, spolicy, new Event[] { new VMStartEvent (vm, req_id, thread_id) }));
				break;
			case EventType.VMDeath:
				queue_event_set (new EventSet (this, spolicy, new Event[] { new VMDeathEvent (vm, req_id, exit_code) }));
				break;
			case EventType.VMDisconnect:
				queue_event_set (new EventSet (this, spolicy, new Event[] { new VMDisconnectEvent (vm, req_id) }));
				break;
			default:
				throw new Exception ();
			}
		}

		//
		// Methods to create instances of mirror objects
		//

		/*
		class MirrorCache<T> {
			static Dictionary <long, T> mirrors;
			static object mirror_lock = new object ();

			internal static T GetMirror (VirtualMachine vm, long id) {
				lock (mirror_lock) {
				if (mirrors == null)
					mirrors = new Dictionary <long, T> ();
				T obj;
				if (!mirrors.TryGetValue (id, out obj)) {
					obj = CreateMirror (vm, id);
					mirrors [id] = obj;
				}
				return obj;
				}
			}

			internal static T CreateMirror (VirtualMachine vm, long id) {
			}
		}
		*/

		// FIXME: When to remove items from the cache ?

		Dictionary <long, MethodMirror> methods;
		object methods_lock = new object ();

		internal MethodMirror GetMethod (long id) {
			lock (methods_lock) {
				if (methods == null)
					methods = new Dictionary <long, MethodMirror> ();
				MethodMirror obj;
				if (id == 0)
					return null;
				if (!methods.TryGetValue (id, out obj)) {
					obj = new MethodMirror (this, id);
					methods [id] = obj;
				}
				return obj;
			}
	    }

		Dictionary <long, AssemblyMirror> assemblies;
		object assemblies_lock = new object ();

		internal AssemblyMirror GetAssembly (long id) {
			lock (assemblies_lock) {
				if (assemblies == null)
					assemblies = new Dictionary <long, AssemblyMirror> ();
				AssemblyMirror obj;
				if (id == 0)
					return null;
				if (!assemblies.TryGetValue (id, out obj)) {
					obj = new AssemblyMirror (this, id);
					assemblies [id] = obj;
				}
				return obj;
			}
	    }

		Dictionary <long, ModuleMirror> modules;
		object modules_lock = new object ();

		internal ModuleMirror GetModule (long id) {
			lock (modules_lock) {
				if (modules == null)
					modules = new Dictionary <long, ModuleMirror> ();
				ModuleMirror obj;
				if (id == 0)
					return null;
				if (!modules.TryGetValue (id, out obj)) {
					obj = new ModuleMirror (this, id);
					modules [id] = obj;
				}
				return obj;
			}
	    }

		Dictionary <long, AppDomainMirror> domains;
		object domains_lock = new object ();

		internal AppDomainMirror GetDomain (long id) {
			lock (domains_lock) {
				if (domains == null)
					domains = new Dictionary <long, AppDomainMirror> ();
				AppDomainMirror obj;
				if (id == 0)
					return null;
				if (!domains.TryGetValue (id, out obj)) {
					obj = new AppDomainMirror (this, id);
					domains [id] = obj;
				}
				return obj;
			}
	    }

		internal void InvalidateAssemblyCaches () {
			lock (domains_lock) {
				foreach (var d in domains.Values)
					d.InvalidateAssembliesCache ();
			}
		}

		Dictionary <long, TypeMirror> types;
		object types_lock = new object ();

		internal TypeMirror GetType (long id) {
			lock (types_lock) {
				if (types == null)
					types = new Dictionary <long, TypeMirror> ();
				TypeMirror obj;
				if (id == 0)
					return null;
				if (!types.TryGetValue (id, out obj)) {
					obj = new TypeMirror (this, id);
					types [id] = obj;
				}
				return obj;
			}
	    }

		internal TypeMirror[] GetTypes (long[] ids) {
			var res = new TypeMirror [ids.Length];
			for (int i = 0; i < ids.Length; ++i)
				res [i] = GetType (ids [i]);
			return res;
		}

		Dictionary <long, ObjectMirror> objects;
		object objects_lock = new object ();

		// Return a mirror if it exists
		// Does not call into the debuggee
		internal T TryGetObject<T> (long id) where T : ObjectMirror {
			lock (objects_lock) {
				if (objects == null)
					objects = new Dictionary <long, ObjectMirror> ();
				ObjectMirror obj;
				objects.TryGetValue (id, out obj);
				return (T)obj;
			}
		}

		internal T GetObject<T> (long id, long domain_id, long type_id) where T : ObjectMirror {
			ObjectMirror obj = null;
			lock (objects_lock) {
				if (objects == null)
					objects = new Dictionary <long, ObjectMirror> ();
				objects.TryGetValue (id, out obj);
			}

			if (obj == null) {
				/*
				 * Obtain the domain/type of the object to determine the type of
				 * object we need to create. Do this outside the lock.
				 */
				if (domain_id == 0 || type_id == 0) {
					if (conn.Version.AtLeast (2, 5)) {
						var info = conn.Object_GetInfo (id);
						domain_id = info.domain_id;
						type_id = info.type_id;
					} else {
						if (domain_id == 0)
							domain_id = conn.Object_GetDomain (id);
						if (type_id == 0)
							type_id = conn.Object_GetType (id);
					}
				}
				AppDomainMirror d = GetDomain (domain_id);
				TypeMirror t = GetType (type_id);

				if (t.Assembly == d.Corlib && t.Namespace == "System.Threading" && t.Name == "Thread")
					obj = new ThreadMirror (this, id, t, d);
				else if (t.Assembly == d.Corlib && t.Namespace == "System" && t.Name == "String")
					obj = new StringMirror (this, id, t, d);
				else if (typeof (T) == typeof (ArrayMirror))
					obj = new ArrayMirror (this, id, t, d);
				else
					obj = new ObjectMirror (this, id, t, d);

				// Publish
				lock (objects_lock) {
					ObjectMirror prev_obj;
					if (objects.TryGetValue (id, out prev_obj))
						obj = prev_obj;
					else
						objects [id] = obj;
				}
			}
			return (T)obj;
	    }

		internal T GetObject<T> (long id) where T : ObjectMirror {
			return GetObject<T> (id, 0, 0);
		}

		internal ObjectMirror GetObject (long objid) {
			return GetObject<ObjectMirror> (objid);
		}

		internal ThreadMirror GetThread (long id) {
			return GetObject <ThreadMirror> (id);
		}

		internal ThreadMirror TryGetThread (long id) {
			return TryGetObject <ThreadMirror> (id);
		}

		Dictionary <long, FieldInfoMirror> fields;
		object fields_lock = new object ();

		internal FieldInfoMirror GetField (long id) {
			lock (fields_lock) {
				if (fields == null)
					fields = new Dictionary <long, FieldInfoMirror> ();
				FieldInfoMirror obj;
				if (id == 0)
					return null;
				if (!fields.TryGetValue (id, out obj)) {
					obj = new FieldInfoMirror (this, id);
					fields [id] = obj;
				}
				return obj;
			}
	    }

		object requests_lock = new object ();

		internal void AddRequest (EventRequest req, int id) {
			lock (requests_lock) {
				requests [id] = req;
			}
		}

		internal void RemoveRequest (EventRequest req, int id) {
			lock (requests_lock) {
				requests.Remove (id);
			}
		}

		internal EventRequest GetRequest (int id) {
			lock (requests_lock) {
				EventRequest obj;
				requests.TryGetValue (id, out obj);
				return obj;
			}
		}

		internal Value DecodeValue (ValueImpl v) {
			return DecodeValue (v, null);
		}

		internal Value DecodeValue (ValueImpl v, Dictionary<int, Value> parent_vtypes) {
			if (v.Value != null) {
				if (Version.AtLeast (2, 46) && v.Type == ElementType.Ptr)
					return new PointerValue(this, GetType(v.Klass), (long)v.Value);
				return new PrimitiveValue (this, v.Value);
			}

			switch (v.Type) {
			case ElementType.Void:
				return null;
			case ElementType.SzArray:
			case ElementType.Array:
				return GetObject<ArrayMirror> (v.Objid);
			case ElementType.String:
				return GetObject<StringMirror> (v.Objid);
			case ElementType.Class:
			case ElementType.Object:
				return GetObject (v.Objid);
			case ElementType.ValueType:
				if (parent_vtypes == null)
					parent_vtypes = new Dictionary<int, Value> ();
				StructMirror vtype;
				if (v.IsEnum)
					vtype = new EnumMirror (this, GetType (v.Klass), (Value[])null);
				else
					vtype = new StructMirror (this, GetType (v.Klass), (Value[])null);
				parent_vtypes [parent_vtypes.Count] = vtype;
				vtype.SetFields (DecodeValues (v.Fields, parent_vtypes));
				parent_vtypes.Remove (parent_vtypes.Count - 1);
				return vtype;
			case (ElementType)ValueTypeId.VALUE_TYPE_ID_NULL:
				return new PrimitiveValue (this, null);
			case (ElementType)ValueTypeId.VALUE_TYPE_ID_PARENT_VTYPE:
				return parent_vtypes [v.Index];
			default:
				throw new NotImplementedException ("" + v.Type);
			}
		}

		internal Value[] DecodeValues (ValueImpl[] values) {
			Value[] res = new Value [values.Length];
			for (int i = 0; i < values.Length; ++i)
				res [i] = DecodeValue (values [i]);
			return res;
		}

		internal Value[] DecodeValues (ValueImpl[] values, Dictionary<int, Value> parent_vtypes) {
			Value[] res = new Value [values.Length];
			for (int i = 0; i < values.Length; ++i)
				res [i] = DecodeValue (values [i], parent_vtypes);
			return res;
		}

		internal ValueImpl EncodeValue (Value v, List<Value> duplicates = null) {
			if (v is PrimitiveValue) {
				object val = (v as PrimitiveValue).Value;
				if (val == null)
					return new ValueImpl { Type = (ElementType)ValueTypeId.VALUE_TYPE_ID_NULL, Objid = 0 };
				else
					return new ValueImpl { Value = val };
			} else if (v is ObjectMirror) {
				return new ValueImpl { Type = ElementType.Object, Objid = (v as ObjectMirror).Id };
			} else if (v is StructMirror) {
				if (duplicates == null)
					duplicates = new List<Value> ();
				if (duplicates.Contains (v))
					return new ValueImpl { Type = (ElementType)ValueTypeId.VALUE_TYPE_ID_NULL, Objid = 0 };
				duplicates.Add (v);

				return new ValueImpl { Type = ElementType.ValueType, Klass = (v as StructMirror).Type.Id, Fields = EncodeFieldValues ((v as StructMirror).Fields, (v as StructMirror).Type.GetFields (), duplicates, 1) };
			} else if (v is PointerValue) {
				PointerValue val = (PointerValue)v;
				return new ValueImpl { Type = ElementType.Ptr, Klass = val.Type.Id, Value = val.Address };
			} else {
				throw new NotSupportedException ("Value of type " + v.GetType());
			}
		}

		internal ValueImpl EncodeValueFixedSize (Value v, List<Value> duplicates, int len_fixed_size) {
			if (v is PrimitiveValue) {
				object val = (v as PrimitiveValue).Value;
				if (val == null)
					return new ValueImpl { Type = (ElementType)ValueTypeId.VALUE_TYPE_ID_NULL, Objid = 0 };
				else
					return new ValueImpl { Value = val , FixedSize = len_fixed_size};
			} else if (v is ObjectMirror) {
				return new ValueImpl { Type = ElementType.Object, Objid = (v as ObjectMirror).Id };
			} else if (v is StructMirror) {
				if (duplicates == null)
					duplicates = new List<Value> ();
				if (duplicates.Contains (v))
					return new ValueImpl { Type = (ElementType)ValueTypeId.VALUE_TYPE_ID_NULL, Objid = 0 };
				duplicates.Add (v);

				return new ValueImpl { Type = ElementType.ValueType, Klass = (v as StructMirror).Type.Id, Fields = EncodeFieldValues ((v as StructMirror).Fields, (v as StructMirror).Type.GetFields (), duplicates, len_fixed_size) };
			} else if (v is PointerValue) {
				PointerValue val = (PointerValue)v;
				return new ValueImpl { Type = ElementType.Ptr, Klass = val.Type.Id, Value = val.Address };
			} else {
				throw new NotSupportedException ("Value of type " + v.GetType());
			}
		}

		internal ValueImpl[] EncodeValues (IList<Value> values, List<Value> duplicates = null) {
			ValueImpl[] res = new ValueImpl [values.Count];
			for (int i = 0; i < values.Count; ++i)
				res [i] = EncodeValue (values [i], duplicates);
			return res;
		}

		internal ValueImpl[] EncodeFieldValues (IList<Value> values, FieldInfoMirror[] field_info, List<Value> duplicates, int fixedSize) {
			ValueImpl[] res = new ValueImpl [values.Count];
			for (int i = 0; i < values.Count; ++i) {
				if (fixedSize > 1 || field_info [i].FixedSize > 1)
					res [i] = EncodeValueFixedSize (values [i], duplicates, fixedSize > 1 ? fixedSize : field_info [i].FixedSize);
				else
					res [i] = EncodeValue (values [i], duplicates);
			}
			return res;
		}

		internal void CheckProtocolVersion (int major, int minor) {
			if (!conn.Version.AtLeast (major, minor))
				throw new NotSupportedException ("This request is not supported by the protocol version implemented by the debuggee.");
		}

		public string GetEnCCapabilities ()
		{
			if (conn.Version.AtLeast (2, 61))
				return conn.VM_EnCCapabilities ();
			return "Baseline";
		}
	}

	class EventHandler : MarshalByRefObject, IEventHandler
	{		
		VirtualMachine vm;

		public EventHandler (VirtualMachine vm) {
			this.vm = vm;
		}

		public void Events (SuspendPolicy suspend_policy, EventInfo[] events) {
			var l = new List<Event> ();

			for (int i = 0; i < events.Length; ++i) {
				EventInfo ei = events [i];
				int req_id = ei.ReqId;
				long thread_id = ei.ThreadId;
				long id = ei.Id;
				long loc = ei.Location;

				switch (ei.EventType) {
				case EventType.VMStart:
					vm.notify_vm_event (EventType.VMStart, suspend_policy, req_id, thread_id, null, 0);
					break;
				case EventType.VMDeath:
					vm.notify_vm_event (EventType.VMDeath, suspend_policy, req_id, thread_id, null, ei.ExitCode);
					break;
				case EventType.ThreadStart:
					vm.InvalidateThreadCache ();
					l.Add (new ThreadStartEvent (vm, req_id, id));
					break;
				case EventType.ThreadDeath:
					// Avoid calling GetThread () since it might call into the debuggee
					// and we can't do that in the event handler
					var thread = vm.TryGetThread (id);
					if (thread != null)
						thread.InvalidateFrames ();
					vm.InvalidateThreadCache ();
					l.Add (new ThreadDeathEvent (vm, req_id, id));
					break;
				case EventType.AssemblyLoad:
					vm.InvalidateAssemblyCaches ();
					l.Add (new AssemblyLoadEvent (vm, req_id, thread_id, id));
					break;
				case EventType.AssemblyUnload:
					vm.InvalidateAssemblyCaches ();
					l.Add (new AssemblyUnloadEvent (vm, req_id, thread_id, id));
					break;
				case EventType.TypeLoad:
					l.Add (new TypeLoadEvent (vm, req_id, thread_id, id));
					break;
				case EventType.MethodEntry:
					l.Add (new MethodEntryEvent (vm, req_id, thread_id, id));
					break;
				case EventType.MethodExit:
					l.Add (new MethodExitEvent (vm, req_id, thread_id, id));
					break;
				case EventType.Breakpoint:
					l.Add (new BreakpointEvent (vm, req_id, thread_id, id, loc));
					break;
				case EventType.Step:
					l.Add (new StepEvent (vm, req_id, thread_id, id, loc));
					break;
				case EventType.Exception:
					l.Add (new ExceptionEvent (vm, req_id, thread_id, id, loc));
					break;
				case EventType.AppDomainCreate:
					l.Add (new AppDomainCreateEvent (vm, req_id, thread_id, id));
					break;
				case EventType.AppDomainUnload:
					l.Add (new AppDomainUnloadEvent (vm, req_id, thread_id, id));
					break;
				case EventType.UserBreak:
					l.Add (new UserBreakEvent (vm, req_id, thread_id));
					break;
				case EventType.UserLog:
					l.Add (new UserLogEvent (vm, req_id, thread_id, ei.Level, ei.Category, ei.Message));
					break;
				case EventType.Crash:
					l.Add (new CrashEvent (vm, req_id, thread_id, ei.Dump, ei.Hash));
					break;
				case EventType.MethodUpdate:
					l.Add (new MethodUpdateEvent (vm, req_id, thread_id, id));
					break;
				}
			}
			
			if (l.Count > 0)
				vm.queue_event_set (new EventSet (vm, suspend_policy, l.ToArray ()));
		}

		public void VMDisconnect (int req_id, long thread_id, string vm_uri) {
			vm.notify_vm_event (EventType.VMDisconnect, SuspendPolicy.None, req_id, thread_id, vm_uri, 0);
        }
    }

	public class CommandException : Exception {

		internal CommandException (ErrorCode error_code, string error_message) : base ("Debuggee returned error code " + error_code + (error_message == null || error_message.Length == 0 ? "." : " - " + error_message + ".")) {
			ErrorCode = error_code;
			ErrorMessage = error_message;
		}

		public ErrorCode ErrorCode {
			get; set;
		}

		public string ErrorMessage {
			get; internal set;
		}
	}

	public class VMNotSuspendedException : InvalidOperationException
	{
		public VMNotSuspendedException () : base ("The vm is not suspended.")
		{
		}
	}
}
