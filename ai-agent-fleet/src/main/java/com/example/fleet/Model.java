package com.example.fleet;

/** The "brain" an agent calls for each task. */
public interface Model {

    String complete(String prompt) throws Exception;
}
