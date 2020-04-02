#ifndef IL2CXX__HEAP_H
#define IL2CXX__HEAP_H

#include "define.h"
#include <atomic>
#include <map>
#include <mutex>
#include <new>
#include <csignal>
#include <cstddef>
#include <unistd.h>
#include <sys/mman.h>

namespace il2cxx
{

template<typename T, typename T_wait>
class t_heap
{
	template<size_t A_rank, size_t A_size>
	struct t_of
	{
		std::atomic<T*> v_chunks = nullptr;
		std::atomic_size_t v_grown = 0;
		std::atomic_size_t v_allocated = 0;
		size_t v_returned = 0;
		size_t v_freed = 0;

		T* f_grow(t_heap& a_heap)
		{
			auto size = 128 << A_rank;
			auto length = size * A_size;
			auto block = static_cast<char*>(mmap(NULL, length, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0));
			auto p = block;
			for (size_t i = 1; i < A_size; ++i) {
				auto q = new(p) T;
				q->v_next = reinterpret_cast<T*>(p += size);
				q->v_rank = A_rank;
			}
			auto q = new(p) T;
			q->v_next = nullptr;
			q->v_rank = A_rank;
			q = reinterpret_cast<T*>(block);
			q->v_cyclic = A_size;
#ifdef IL2CXX__STACK_SCAN_DIRECT
			pthread_sigmask(SIG_BLOCK, &a_heap.v_sigusr1, NULL);
#endif
			a_heap.v_mutex.lock();
			a_heap.v_blocks.emplace(q, length);
			a_heap.v_mutex.unlock();
#ifdef IL2CXX__STACK_SCAN_DIRECT
			pthread_sigmask(SIG_UNBLOCK, &a_heap.v_sigusr1, NULL);
#endif
			v_grown.fetch_add(A_size, std::memory_order_relaxed);
			return q;
		}
		T* f_allocate(t_heap* a_heap)
		{
			auto p = v_chunks.load(std::memory_order_relaxed);
			while (p && !v_chunks.compare_exchange_weak(p, p->v_previous, std::memory_order_acquire));
			if (!p) {
				if (!a_heap) return nullptr;
				p = f_grow(*a_heap);
			}
			v_allocated.fetch_add(p->v_cyclic, std::memory_order_relaxed);
			return p;
		}
		void f_return(size_t a_n)
		{
			v_head<A_rank>->v_cyclic = a_n;
			v_head<A_rank>->v_previous = v_chunks.load(std::memory_order_relaxed);
			while (!v_chunks.compare_exchange_weak(v_head<A_rank>->v_previous, v_head<A_rank>, std::memory_order_release));
			v_head<A_rank> = nullptr;
			v_returned += a_n;
		}
		void f_return()
		{
			size_t n = 0;
			for (auto p = v_head<A_rank>; p; p = p->v_next) ++n;
			if (n > 0) f_return(n);
		}
		void f_flush()
		{
			f_return(v_freed);
			v_freed = 0;
		}
		void f_free(T* a_p)
		{
			a_p->v_next = v_head<A_rank>;
			v_head<A_rank> = a_p;
			if (++v_freed >= A_size) f_flush();
		}
		size_t f_live() const
		{
			return v_allocated.load(std::memory_order_relaxed) - v_returned - v_freed;
		}
	};

	template<size_t A_rank>
	static IL2CXX__PORTABLE__THREAD T* v_head;

	T_wait v_wait;
	std::map<T*, size_t> v_blocks;
#ifdef IL2CXX__STACK_SCAN_DIRECT
	sigset_t v_sigusr1;
#endif
	std::mutex v_mutex;
	t_of<0, 1024 * 64> v_of0;
	t_of<1, 1024 * 16> v_of1;
	t_of<2, 1024 * 4> v_of2;
	t_of<3, 1024> v_of3;
	t_of<4, 1024 / 4> v_of4;
	t_of<5, 1024 / 16> v_of5;
	t_of<6, 1024 / 64> v_of6;
	size_t v_allocated = 0;
	size_t v_freed = 0;

	template<size_t A_rank, size_t A_size>
	T* f_allocate(t_of<A_rank, A_size>& a_of)
	{
		auto p = v_head<A_rank>;
		if (!p) {
			p = a_of.f_allocate(nullptr);
			if (!p) {
				v_wait();
				p = a_of.f_allocate(this);
			}
		}
		v_head<A_rank> = p->v_next;
		return p;
	}

public:
	t_heap(T_wait&& a_wait) : v_wait(std::move(a_wait))
	{
#ifdef IL2CXX__STACK_SCAN_DIRECT
		sigemptyset(&v_sigusr1);
		sigaddset(&v_sigusr1, SIGUSR1);
#endif
	}
	~t_heap()
	{
		for (auto& x : v_blocks) munmap(x.first, x.second);
	}
	size_t f_live() const
	{
		return v_of0.f_live() + v_of1.f_live() + v_of2.f_live() + v_of3.f_live() + v_of4.f_live() + v_of5.f_live() + v_of6.f_live() + v_allocated - v_freed;
	}
	template<typename T_each>
	void f_statistics(T_each a_each) const
	{
		a_each(0, v_of0.v_grown.load(std::memory_order_relaxed), v_of0.v_allocated.load(std::memory_order_relaxed), v_of0.v_returned);
		a_each(1, v_of1.v_grown.load(std::memory_order_relaxed), v_of1.v_allocated.load(std::memory_order_relaxed), v_of1.v_returned);
		a_each(2, v_of2.v_grown.load(std::memory_order_relaxed), v_of2.v_allocated.load(std::memory_order_relaxed), v_of2.v_returned);
		a_each(3, v_of3.v_grown.load(std::memory_order_relaxed), v_of3.v_allocated.load(std::memory_order_relaxed), v_of3.v_returned);
		a_each(4, v_of4.v_grown.load(std::memory_order_relaxed), v_of4.v_allocated.load(std::memory_order_relaxed), v_of4.v_returned);
		a_each(5, v_of5.v_grown.load(std::memory_order_relaxed), v_of5.v_allocated.load(std::memory_order_relaxed), v_of5.v_returned);
		a_each(6, v_of6.v_grown.load(std::memory_order_relaxed), v_of6.v_allocated.load(std::memory_order_relaxed), v_of6.v_returned);
		a_each(57, 0, v_allocated, v_freed);
	}
	T* f_allocate(size_t a_size)
	{
		size_t n = a_size >> 7;
		if (n == 0) return f_allocate(v_of0);
		n >>= 1;
		if (n == 0) return f_allocate(v_of1);
		n >>= 1;
		if (n == 0) return f_allocate(v_of2);
		n >>= 1;
		if (n == 0) return f_allocate(v_of3);
		n >>= 1;
		if (n == 0) return f_allocate(v_of4);
		n >>= 1;
		if (n == 0) return f_allocate(v_of5);
		n >>= 1;
		if (n == 0) return f_allocate(v_of6);
		auto p = new(mmap(NULL, a_size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0)) T;
		p->v_rank = 57;
#ifdef IL2CXX__STACK_SCAN_DIRECT
		pthread_sigmask(SIG_BLOCK, &v_sigusr1, NULL);
#endif
		v_mutex.lock();
		v_blocks.emplace(p, a_size);
		++v_allocated;
		v_mutex.unlock();
#ifdef IL2CXX__STACK_SCAN_DIRECT
		pthread_sigmask(SIG_UNBLOCK, &v_sigusr1, NULL);
#endif
		return p;
	}
	void f_return()
	{
		v_of0.f_return();
		v_of1.f_return();
		v_of2.f_return();
		v_of3.f_return();
		v_of4.f_return();
		v_of5.f_return();
		v_of6.f_return();
	}
	void f_flush()
	{
		if (v_of0.v_freed > 0) v_of0.f_flush();
		if (v_of1.v_freed > 0) v_of1.f_flush();
		if (v_of2.v_freed > 0) v_of2.f_flush();
		if (v_of3.v_freed > 0) v_of3.f_flush();
		if (v_of4.v_freed > 0) v_of4.f_flush();
		if (v_of5.v_freed > 0) v_of5.f_flush();
		if (v_of6.v_freed > 0) v_of6.f_flush();
	}
	void f_free(T* a_p)
	{
		switch (a_p->v_rank) {
		case 0:
			v_of0.f_free(a_p);
			break;
		case 1:
			v_of1.f_free(a_p);
			break;
		case 2:
			v_of2.f_free(a_p);
			break;
		case 3:
			v_of3.f_free(a_p);
			break;
		case 4:
			v_of4.f_free(a_p);
			break;
		case 5:
			v_of5.f_free(a_p);
			break;
		case 6:
			v_of6.f_free(a_p);
			break;
		default:
			v_mutex.lock();
			auto i = v_blocks.find(a_p);
			auto n = i->second;
			v_blocks.erase(i);
			v_mutex.unlock();
			munmap(a_p, n);
			++v_freed;
		}
	}
	std::mutex& f_mutex()
	{
		return v_mutex;
	}
	T* f_find(void* a_p)
	{
		auto i = v_blocks.lower_bound(static_cast<T*>(a_p));
		if (i == v_blocks.end() || i->first != a_p) {
			if (i == v_blocks.begin()) return nullptr;
			--i;
		}
		auto j = static_cast<char*>(a_p) - reinterpret_cast<char*>(i->first);
		return j < i->second && (j & (128 << i->first->v_rank) - 1) == 0 ? static_cast<T*>(a_p) : nullptr;
	}
};

template<typename T, typename T_wait>
template<size_t A_rank>
IL2CXX__PORTABLE__THREAD T* t_heap<T, T_wait>::v_head;

}

#endif