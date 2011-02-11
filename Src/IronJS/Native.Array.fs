﻿namespace IronJS.Native

open System
open IronJS
open IronJS.Support.Aliases
open IronJS.DescriptorAttrs
open IronJS.Utils.Patterns

//------------------------------------------------------------------------------
// 15.4
module Array =

  type private Sort = Func<FunctionObject, CommonObject, BoxedValue, BoxedValue, BoxedValue>

  //----------------------------------------------------------------------------
  // 15.4.2
  let internal constructor' (f:FunctionObject) (_:CommonObject) (args:BoxedValue array) =
    if args.Length = 1 then
      let number = TypeConverter2.ToNumber args.[0]
      let size = TypeConverter2.ToUInt32 number
      f.Env.NewArray(size)

    else
      let size = args.Length |> uint32
      let array = f.Env.NewArray(size)
      
      Array.iteri (fun i (value:BoxedValue) -> 
        array.Put(uint32 i, value)) args

      array
      
  //----------------------------------------------------------------------------
  let internal join (f:FunctionObject) (this:CommonObject) (separator:BoxedValue) =
  
    let separator =
      if separator.Tag |> Utils.Box.isUndefined 
        then "," else separator |> TypeConverter2.ToString

    match this with
    | IsArray array ->

      match array with
      | IsDense ->
        let toString (x:Descriptor) = 
          if x.HasValue then TypeConverter2.ToString x.Value else ""

        String.Join(separator, array.Dense |> Array.map toString)

      | IsSparse ->
        let items = new MutableList<string>();
        let mutable i = 0u

        while i < array.Length do
          match array.Sparse.TryGetValue i with
          | true, box -> items.Add (box |> TypeConverter2.ToString) 
          | _ -> items.Add ""

          i <- i + 1u

        String.Join(separator, items)

    | _ ->
      let length = this.GetLength()
      let items = new MutableList<string>()
      
      let mutable index = 0u
      while index < length do
        items.Add(this.Get index |> TypeConverter2.ToString)  
        index <- index + 1u

      String.Join(separator, items)
      
  //----------------------------------------------------------------------------
  let internal concat (f:FunctionObject) (this:CommonObject) (args:BoxedValue array) =
    let items = new MutableList<BoxedValue>(this.CollectIndexValues())

    for arg in args do
      if arg.Tag |> Utils.Box.isObject 
        then items.AddRange(arg.Object.CollectIndexValues())
        else items.Add arg

    let array = 
      f.Env.NewArray(uint32 items.Count) :?> ArrayObject
    
    let mutable index = 0u
    while index < array.Length do
      let i = int index
      array.Dense.[i].Value <- items.[i]
      array.Dense.[i].HasValue <- true
      index <- index + 1u

    array :> CommonObject
    
  //----------------------------------------------------------------------------
  let internal pop (this:CommonObject) =
    match this with
    | IsArray a ->
      let index = a.Length - 1u

      if index >= a.Length then 
        Utils.BoxedConstants.Undefined

      else
        let item = 
          match a with
          | IsDense ->
            let descriptor = a.Dense.[int index]
            if descriptor.HasValue then 
              descriptor.Value 

            else 
              if a.HasPrototype 
                then a.Prototype.Get index
                else Utils.BoxedConstants.Undefined

          | IsSparse ->
            match a.Sparse.TryGetValue index with
            | true, box -> box
            | _ -> 
              if a.HasPrototype 
                then a.Prototype.Get index
                else Utils.BoxedConstants.Undefined

        a.Delete index |> ignore
        item

    | _ -> 
      let length = this.GetLength()
      
      if length = 0u then
        this.Put("length", 0.0)
        Utils.BoxedConstants.Undefined

      else
        let index = length - 1u
        let item = this.Get index
        this.Delete index |> ignore
        this.Put("length", double index)
        item
    
  //----------------------------------------------------------------------------
  let internal push (this:CommonObject) (args:BoxedValue array) =
    let mutable length = this.GetLength()

    for arg in args do 
      this.Put(length, arg)
      length <- length + 1u

    if not (this :? ArrayObject) then
      this.Put("length", double length)

    this.GetLength() |> double
    
  //----------------------------------------------------------------------------
  let internal reverse (this:CommonObject) =
    match this with
    | IsArray a ->
      match a with
      | IsDense -> a.Dense |> Array.Reverse 
      | IsSparse -> 
        let sparse = new MutableSorted<uint32, BoxedValue>()
        for kvp in a.Sparse do
          sparse.Add(a.Length - kvp.Key - 1u, kvp.Value)
        a.Sparse <- sparse

      a :> CommonObject

    | _ -> 
      let rec reverseObject (o:CommonObject) length (index:uint32) items =
        if index >= length then items
        else
          if o.Has index then
            let item = o.Get index
            let newIndex = length - index - 1u
            o.Delete index |> ignore
            reverseObject o length (index+1u) ((newIndex, item) :: items)

          else
            reverseObject o length (index+1u) items

            
      let length = this.GetLength()
      let items = reverseObject this length 0u []

      for index, item in items do
        this.Put(index, item)

      this
    
  //----------------------------------------------------------------------------
  let internal shift (this:CommonObject) =

    let updateArrayLength (a:ArrayObject) =
      a.Length <- a.Length - 1u
      a.Put("length", double a.Length)

    match this with
    | IsArray a ->
      if a.Length = 0u then Utils.BoxedConstants.Undefined
      else
        match a with
        | IsDense ->
          let item =  
            if a.Dense.[0].HasValue 
              then a.Dense.[0].Value

            elif a.HasPrototype 
              then a.Prototype.Get 0u
              else Utils.BoxedConstants.Undefined

          //Remove first element of dense array, also updates indexes
          a.Dense <- a.Dense |> Dlr.ArrayUtils.RemoveFirst
          updateArrayLength a
          item

        | IsSparse ->
          let item = 
            match a.Sparse.TryGetValue 0u with
            | true, item -> a.Sparse.Remove 0u |> ignore; item
            | _ -> 
              if a.HasPrototype 
                then a.Prototype.Get 0u
                else Utils.BoxedConstants.Undefined

          //Update sparse indexes
          for kvp in a.Sparse do
            a.Sparse.Remove kvp.Key |> ignore
            a.Sparse.Add(kvp.Key - 1u, kvp.Value)

          updateArrayLength a
          item

    | _ -> 
      let length = this.GetLength()
      if length = 0u then Utils.BoxedConstants.Undefined
      else
        let item = this.Get 0u
        this.Delete 0u |> ignore
        item
    
  //----------------------------------------------------------------------------
  let internal slice (f:FunctionObject) (this:CommonObject) (start:double) (end':BoxedValue) =
    let start = start |> TypeConverter2.ToInteger

    let constrainStartAndEnd st en le = 
      let st = if st < 0 then st + le elif st > le then le else st
      let en = if en < 0 then en + le elif en > le then le else en
      st, en

    let getEnd (en:BoxedValue) (le:int) =
        if en.Tag |> Utils.Box.isUndefined 
          then le else en |> TypeConverter2.ToInteger

    let length = this.GetLength() |> int
    let end' = getEnd end' length
    let start, end' = constrainStartAndEnd start end' length
    let size = end' - start
    let absSize = if size < 0 then 0 else size
    let array = f.Env.NewArray(uint32 absSize) :?> ArrayObject

    for i = 0 to (size-1) do
      // array.Methods.GetIndex.Invoke(this, uint32 (start+i))
      let item = array.Get(uint32 (start+i))
      array.Put(uint32 i, item)
      
    array :> CommonObject

  type private SparseComparer(cmp) =
    interface System.Collections.Generic.IComparer<bool * BoxedValue> with
      member x.Compare((_, a), (_, b)) = cmp a b

  //----------------------------------------------------------------------------
  let internal sort (f:FunctionObject) (this:CommonObject) (cmp:BoxedValue) =
    
    (*
    // Note that the implementation for sorting sparse arrays is incredibly
    // slow and consumes a lot of memory. This comes from the fact that
    // I've cheated and implemented sparse arrays using a sorted dictionary.
    // 
    // This will be addressed when I get time to replace the sparse array
    // implementation with something more space effective (possibly a BitTrie)
    // that also gives me access to the internals of the data structure 
    // allowing me to sort the sparse array in place.
    *)

    let denseSortFunc (f:FunctionObject) =
      let sort = f.Compiler.Compile<Sort> f

      fun (x:Descriptor) (y:Descriptor) -> 
        let x = if x.HasValue then x.Value else Utils.BoxedConstants.Undefined
        let y = if y.HasValue then y.Value else Utils.BoxedConstants.Undefined
        let result = sort.Invoke(f, f.Env.Globals, x, y)
        result |> TypeConverter2.ToNumber |> int

    let denseSortDefault (x:Descriptor) (y:Descriptor) =
      let x = if x.HasValue then x.Value else Utils.BoxedConstants.Undefined
      let y = if y.HasValue then y.Value else Utils.BoxedConstants.Undefined
      String.Compare(TypeConverter2.ToString x, TypeConverter2.ToString y)

    let sparseSort (cmp:SparseComparer) (length:uint32) (vals:SparseArray) =
      let items = new MutableList<bool * BoxedValue>()
      let newArray = new SparseArray()

      let i = ref 0u
      while !i < length do
          
        match vals.TryGetValue !i with
        | true, box -> items.Add(true, box)
        | _ -> items.Add(false, Utils.BoxedConstants.Undefined)

        i := !i + 1u

      items.Sort cmp

      i := 0u
      for org, item in items do
        if org then 
          newArray.Add(!i, item)
        i := !i + 1u

      newArray

    let sparseSortFunc (f:FunctionObject) =
      let sort = f.Compiler.Compile<Sort> f
      fun (x:BoxedValue) (y:BoxedValue) -> 
        let result = sort.Invoke(f, f.Env.Globals, x, y)
        result |> TypeConverter2.ToNumber |> int

    let sparseSortDefault (x:BoxedValue) (y:BoxedValue) =
      String.Compare(TypeConverter2.ToString x, TypeConverter2.ToString y)

    match this with
    | IsArray a ->
      match cmp.Tag with
      | TypeTags.Function ->
        match a with
        | IsDense -> a.Dense |> Array.sortInPlaceWith (denseSortFunc cmp.Func)
        | IsSparse -> 
          let sort = f.Compiler.Compile<Sort>(f)
          let cmp = new SparseComparer(sparseSortFunc cmp.Func)
          a.Sparse <- sparseSort cmp a.Length a.Sparse

      | _ ->
        match a with
        | IsDense -> a.Dense |> Array.sortInPlaceWith denseSortDefault
        | IsSparse ->
          let sort = f.Compiler.Compile<Sort>(f)
          let cmp = new SparseComparer(sparseSortDefault)
          a.Sparse <- sparseSort cmp a.Length a.Sparse

    | _ -> failwith ".sort currently does not support non-arrays"

    this
    
  //----------------------------------------------------------------------------
  let internal unshift (f:FunctionObject) (this:CommonObject) (args:BoxedValue array) =
    match this with
    | IsArray array ->

      match array with
      | IsDense ->
        let minLength = int array.Length + args.Length

        let newDense =
          if minLength > array.Dense.Length 
            then Array.zeroCreate minLength 
            else array.Dense

        Array.Copy(array.Dense, 0, newDense, args.Length, array.Dense.Length)
        array.Dense <- newDense
        
        for i = 0 to args.Length-1 do
          newDense.[i].Value <- args.[i]
          newDense.[i].HasValue <- true

        array.UpdateLength(uint32 args.Length + array.Length)

      | IsSparse -> 

        let mutable index = array.Length - 1u
        let offset = uint32 args.Length

        while index >= 0u && index <= array.Length do
          
          match array.Sparse.TryGetValue index with
          | true, box ->
            array.Sparse.Remove index |> ignore
            array.Sparse.Add(index + offset, box)

          | _ -> ()

          index <- index - 1u
          
        for i = 0 to args.Length-1 do
          array.Sparse.Add(uint32 i, args.[i])

        array.UpdateLength(offset + array.Length)
        
    | _ -> failwith ".unshift currently does not support non-arrays"

    this
      
  //----------------------------------------------------------------------------
  let internal toString (f:FunctionObject) (a:CommonObject) =
    a |> Utils.mustBe Classes.Array f.Env
    join f a Utils.BoxedConstants.Undefined

  let internal toLocaleString = toString

  //----------------------------------------------------------------------------
  let setupConstructor (env:Environment) =
    let ctor = new Func<FunctionObject, CommonObject, BoxedValue array, CommonObject>(constructor')
    let ctor = Api.HostFunction.create env ctor

    ctor.ConstructorMode <- ConstructorModes.Host
    ctor.Put("prototype", env.Prototypes.Array, Immutable)

    env.Globals.Put("Array", ctor)
    env.Constructors <- {env.Constructors with Array = ctor}
    
  //----------------------------------------------------------------------------
  let createPrototype (env:Environment) objPrototype =
    let prototype = env.NewArray()
    prototype.Prototype <- objPrototype
    prototype.Class <- Classes.Array
    prototype
    
  //----------------------------------------------------------------------------
  let setupPrototype (env:Environment) =
    let proto = env.Prototypes.Array
    proto.Put("constructor", env.Constructors.Array, DontEnum)
    
    let toString = new Func<FunctionObject, CommonObject, string>(toString)
    let toString = Api.HostFunction.create env toString
    proto.Put("toString", toString, DontEnum)

    let toLocaleString = new Func<FunctionObject, CommonObject, string>(toLocaleString)
    let toLocaleString = Api.HostFunction.create env toLocaleString
    proto.Put("toLocaleString", toLocaleString, DontEnum)

    let concat = new Func<FunctionObject, CommonObject, BoxedValue array, CommonObject>(concat)
    let concat = Api.HostFunction.create env concat
    proto.Put("concat", concat, DontEnum)
    
    let join = new Func<FunctionObject, CommonObject, BoxedValue, string>(join)
    let join = Api.HostFunction.create env join
    proto.Put("join", join, DontEnum)
    
    let pop = new Func<CommonObject, BoxedValue>(pop)
    let pop = Api.HostFunction.create env pop
    proto.Put("pop", pop, DontEnum)

    let push = new Func<CommonObject, BoxedValue array, double>(push)
    let push = Api.HostFunction.create env push
    proto.Put("push", push, DontEnum)

    let reverse = new Func<CommonObject, CommonObject>(reverse)
    let reverse = Api.HostFunction.create env reverse
    proto.Put("reverse", reverse, DontEnum)
    
    let shift = new Func<CommonObject, BoxedValue>(shift)
    let shift = Api.HostFunction.create env shift
    proto.Put("shift", shift, DontEnum)

    let slice = new Func<FunctionObject, CommonObject, double, BoxedValue, CommonObject>(slice)
    let slice = Api.HostFunction.create env slice
    proto.Put("slice", slice, DontEnum)

    let sort = new Func<FunctionObject, CommonObject, BoxedValue, CommonObject>(sort)
    let sort = Api.HostFunction.create env sort
    proto.Put("sort", sort, DontEnum)

    let unshift = new Func<FunctionObject, CommonObject, BoxedValue array, CommonObject>(unshift)
    let unshift = Api.HostFunction.create env unshift
    proto.Put("unshift", unshift, DontEnum)

