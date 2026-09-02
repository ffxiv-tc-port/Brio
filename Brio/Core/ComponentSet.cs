
//
// This code is from the Zero Electric Framework and provided to you under the MIT license 
//

//
// MIT License
// 
// Copyright (c) 2025 Ken M (minmoose), Zero Electric
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//  
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//

using System;
using System.Collections;
using System.Collections.Generic;

namespace Brio.Core;

public class ComponentSet<T> : IEnumerable<T>, IDisposable
{
    const int DefaultStartingSize = 50;
    const int DefaultResizeBy = 50;

    internal Queue<int> AvailableIndices = new(10);

    public int NextAvailableIndex { get; private set; } = 0;
    public int ActiveCount { get; private set; } = 0;

    public T[] Components;

    public ComponentSet()
    {
        Components = new T[DefaultStartingSize];
    }
    public ComponentSet(int StartingSize)
    {
        Components = new T[StartingSize];
    }

    public int Add(T component)
    {
        int indexAddress;

        if(AvailableIndices.Count > 0)
        {
            indexAddress = AvailableIndices.Dequeue();
        }
        else
        {
            indexAddress = NextAvailableIndex++;

            if(NextAvailableIndex == Components.Length)
                Resize();
        }

        Components[indexAddress] = component;
        ActiveCount++;

        return indexAddress;
    }

    public void ReplaceItem(int address, T item)
    {
        // 🔴 界外時必須真的停手:原本只印 Log.Fatal 就繼續往下走。
        //    address >= NextAvailableIndex 時會把元件寫進一個從沒配過的槽位 ——
        //    GetEnumerator 只走到 NextAvailableIndex,那格永遠列舉不到,也永遠不會被回收。
        if(address < 0 || address >= NextAvailableIndex)
        {
            Brio.Log.Fatal($"(ComponentSet::ReplaceItem) [{address}] Address is out of range.");
            return;
        }

        Components[address] = item;
    }

    public void Remove(int address)
    {
        // 🔴 界外時必須真的停手:原本只印 Log.Fatal 就繼續往下走,後果是毒化空號佇列。
        //    address >= NextAvailableIndex 時 Components[address] 本來就是 default(寫進去是空操作),
        //    真正的損害是 AvailableIndices.Enqueue(address) 把一個從沒配過的號碼塞進空號佇列:
        //    之後 Add 會拿到它,寫進去的元件位在 NextAvailableIndex 之外 ⇒ 列舉不到;
        //    對它再 Remove 一次又會重複入列 ⇒ 兩次 Add 拿到同一格,後者覆蓋前者。
        //    ActiveCount 也會一路失準。
        //    (address < 0 那一支原本是走到 Components[-1] 直接擲 IndexOutOfRangeException,
        //     改成與正號一致:記錄 Fatal 後不動任何狀態。)
        if(address < 0 || address >= NextAvailableIndex)
        {
            Brio.Log.Fatal($"(ComponentSet::Remove) [{address}] Address is out of range.");
            return;
        }

        Components[address] = default!;
        AvailableIndices.Enqueue(address);
        ActiveCount--;
    }

    public void Clear()
    {        
        Array.Clear(Components, 0, Components.Length);

        AvailableIndices.Clear();
        NextAvailableIndex = 0;
        ActiveCount = 0;
    }

    private void Resize(int size = DefaultResizeBy)
    {
        Array.Resize(ref Components, Components.Length + size);
    }

    public IEnumerator<T> GetEnumerator()
    {
        for(int i = 0; i < NextAvailableIndex; i++)
        {
            if(!EqualityComparer<T>.Default.Equals(Components[i], default!))
                yield return Components[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public void Dispose()
    {
        Clear();

        GC.SuppressFinalize(this);
    }
}
